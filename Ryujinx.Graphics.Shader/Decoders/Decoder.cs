using Ryujinx.Graphics.Shader.Instructions;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;

using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Decoders
{
    static class Decoder
    {
        private delegate object OpActivator(InstEmitter emitter, ulong address, long opCode);

        private static ConcurrentDictionary<Type, OpActivator> _opActivators;

        static Decoder()
        {
            _opActivators = new ConcurrentDictionary<Type, OpActivator>();
        }

        public static Block[] Decode(Span<byte> code, ulong headerSize)
        {
            List<Block> blocks = new List<Block>();

            Queue<Block> workQueue = new Queue<Block>();

            Dictionary<ulong, Block> visited = new Dictionary<ulong, Block>();

            ulong maxAddress = (ulong)code.Length - headerSize;

            Block GetBlock(ulong blkAddress)
            {
                if (!visited.TryGetValue(blkAddress, out Block block))
                {
                    block = new Block(blkAddress);

                    workQueue.Enqueue(block);

                    visited.Add(blkAddress, block);
                }

                return block;
            }

            GetBlock(0);

            while (workQueue.TryDequeue(out Block currBlock))
            {
                // Check if the current block is inside another block.
                if (BinarySearch(blocks, currBlock.Address, out int nBlkIndex))
                {
                    Block nBlock = blocks[nBlkIndex];

                    if (nBlock.Address == currBlock.Address)
                    {
                        throw new InvalidOperationException("Found duplicate block address on the list.");
                    }

                    nBlock.Split(currBlock);

                    blocks.Insert(nBlkIndex + 1, currBlock);

                    continue;
                }

                // If we have a block after the current one, set the limit address.
                ulong limitAddress = maxAddress;

                if (nBlkIndex != blocks.Count)
                {
                    Block nBlock = blocks[nBlkIndex];

                    int nextIndex = nBlkIndex + 1;

                    if (nBlock.Address < currBlock.Address && nextIndex < blocks.Count)
                    {
                        limitAddress = blocks[nextIndex].Address;
                    }
                    else if (nBlock.Address > currBlock.Address)
                    {
                        limitAddress = blocks[nBlkIndex].Address;
                    }
                }

                FillBlock(code, currBlock, limitAddress, headerSize);

                if (currBlock.OpCodes.Count != 0)
                {
                    // We should have blocks for all possible branch targets,
                    // including those from SSY/PBK instructions.
                    foreach (OpCodePush pushOp in currBlock.PushOpCodes)
                    {
                        if (pushOp.GetAbsoluteAddress() >= maxAddress)
                        {
                            return null;
                        }

                        GetBlock(pushOp.GetAbsoluteAddress());
                    }

                    // Set child blocks. "Branch" is the block the branch instruction
                    // points to (when taken), "Next" is the block at the next address,
                    // executed when the branch is not taken. For Unconditional Branches
                    // or end of program, Next is null.
                    OpCode lastOp = currBlock.GetLastOp();

                    if (lastOp is OpCodeBranch opBr)
                    {
                        if (opBr.GetAbsoluteAddress() >= maxAddress)
                        {
                            return null;
                        }

                        currBlock.Branch = GetBlock(opBr.GetAbsoluteAddress());
                    }
                    else if (lastOp is OpCodeBranchIndir opBrIndir)
                    {
                        // An indirect branch could go anywhere, we don't know the target.
                        // Those instructions are usually used on a switch to jump table
                        // compiler optimization, and in those cases the possible targets
                        // seems to be always right after the BRX itself. We can assume
                        // that the possible targets are all the blocks in-between the
                        // instruction right after the BRX, and the common target that
                        // all the "cases" should eventually jump to, acting as the
                        // switch break.
                        Block firstTarget = GetBlock(currBlock.EndAddress);

                        firstTarget.BrIndir = opBrIndir;

                        opBrIndir.PossibleTargets.Add(firstTarget);
                    }

                    if (!IsUnconditionalBranch(lastOp))
                    {
                        currBlock.Next = GetBlock(currBlock.EndAddress);
                    }
                }

                // Insert the new block on the list (sorted by address).
                if (blocks.Count != 0)
                {
                    Block nBlock = blocks[nBlkIndex];

                    blocks.Insert(nBlkIndex + (nBlock.Address < currBlock.Address ? 1 : 0), currBlock);
                }
                else
                {
                    blocks.Add(currBlock);
                }

                // Do we have a block after the current one?
                if (!IsExit(currBlock.GetLastOp()) && currBlock.BrIndir != null && currBlock.EndAddress < maxAddress)
                {
                    bool targetVisited = visited.ContainsKey(currBlock.EndAddress);

                    Block possibleTarget = GetBlock(currBlock.EndAddress);

                    currBlock.BrIndir.PossibleTargets.Add(possibleTarget);

                    if (!targetVisited)
                    {
                        possibleTarget.BrIndir = currBlock.BrIndir;
                    }
                }
            }

            foreach (Block block in blocks.Where(x => x.PushOpCodes.Count != 0))
            {
                for (int pushOpIndex = 0; pushOpIndex < block.PushOpCodes.Count; pushOpIndex++)
                {
                    PropagatePushOp(visited, block, pushOpIndex);
                }
            }

            return blocks.ToArray();
        }

        private static bool BinarySearch(List<Block> blocks, ulong address, out int index)
        {
            index = 0;

            int left  = 0;
            int right = blocks.Count - 1;

            while (left <= right)
            {
                int size = right - left;

                int middle = left + (size >> 1);

                Block block = blocks[middle];

                index = middle;

                if (address >= block.Address && address < block.EndAddress)
                {
                    return true;
                }

                if (address < block.Address)
                {
                    right = middle - 1;
                }
                else
                {
                    left = middle + 1;
                }
            }

            return false;
        }

        private static void FillBlock(
            Span<byte> code,
            Block      block,
            ulong      limitAddress,
            ulong      startAddress)
        {
            ulong address = block.Address;

            do
            {
                if (address + 7 >= limitAddress)
                {
                    break;
                }

                // Ignore scheduling instructions, which are written every 32 bytes.
                if ((address & 0x1f) == 0)
                {
                    address += 8;

                    continue;
                }

                uint word0 = BinaryPrimitives.ReadUInt32LittleEndian(code.Slice((int)(startAddress + address)));
                uint word1 = BinaryPrimitives.ReadUInt32LittleEndian(code.Slice((int)(startAddress + address + 4)));

                ulong opAddress = address;

                address += 8;

                long opCode = word0 | (long)word1 << 32;

                (InstEmitter emitter, Type opCodeType) = OpCodeTable.GetEmitter(opCode);

                if (emitter == null)
                {
                    // TODO: Warning, illegal encoding.

                    block.OpCodes.Add(new OpCode(null, opAddress, opCode));

                    continue;
                }

                OpCode op = MakeOpCode(opCodeType, emitter, opAddress, opCode);

                block.OpCodes.Add(op);
            }
            while (!IsBranch(block.GetLastOp()));

            block.EndAddress = address;

            block.UpdatePushOps();
        }

        private static bool IsUnconditionalBranch(OpCode opCode)
        {
            return IsUnconditional(opCode) && IsBranch(opCode);
        }

        private static bool IsUnconditional(OpCode opCode)
        {
            if (opCode is OpCodeExit op && op.Condition != Condition.Always)
            {
                return false;
            }

            return opCode.Predicate.Index == RegisterConsts.PredicateTrueIndex && !opCode.InvertPredicate;
        }

        private static bool IsBranch(OpCode opCode)
        {
            return (opCode is OpCodeBranch opBranch && !opBranch.PushTarget) ||
                    opCode is OpCodeBranchIndir                              ||
                    opCode is OpCodeBranchPop                                ||
                    opCode is OpCodeExit;
        }

        private static bool IsExit(OpCode opCode)
        {
            return opCode is OpCodeExit;
        }

        private static OpCode MakeOpCode(Type type, InstEmitter emitter, ulong address, long opCode)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            OpActivator createInstance = _opActivators.GetOrAdd(type, CacheOpActivator);

            return (OpCode)createInstance(emitter, address, opCode);
        }

        private static OpActivator CacheOpActivator(Type type)
        {
            Type[] argTypes = new Type[] { typeof(InstEmitter), typeof(ulong), typeof(long) };

            DynamicMethod mthd = new DynamicMethod($"Make{type.Name}", type, argTypes);

            ILGenerator generator = mthd.GetILGenerator();

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_2);
            generator.Emit(OpCodes.Newobj, type.GetConstructor(argTypes));
            generator.Emit(OpCodes.Ret);

            return (OpActivator)mthd.CreateDelegate(typeof(OpActivator));
        }

        private struct PathBlockState
        {
            public Block Block { get; }

            private enum RestoreType
            {
                None,
                PopPushOp,
                PushBranchOp
            }

            private RestoreType _restoreType;

            private ulong _restoreValue;

            public bool ReturningFromVisit => _restoreType != RestoreType.None;

            public PathBlockState(Block block)
            {
                Block         = block;
                _restoreType  = RestoreType.None;
                _restoreValue = 0;
            }

            public PathBlockState(int oldStackSize)
            {
                Block         = null;
                _restoreType  = RestoreType.PopPushOp;
                _restoreValue = (ulong)oldStackSize;
            }

            public PathBlockState(ulong syncAddress)
            {
                Block         = null;
                _restoreType  = RestoreType.PushBranchOp;
                _restoreValue = syncAddress;
            }

            public void RestoreStackState(Stack<ulong> branchStack)
            {
                if (_restoreType == RestoreType.PushBranchOp)
                {
                    branchStack.Push(_restoreValue);
                }
                else if (_restoreType == RestoreType.PopPushOp)
                {
                    while (branchStack.Count > (uint)_restoreValue)
                    {
                        branchStack.Pop();
                    }
                }
            }
        }

        private static void PropagatePushOp(Dictionary<ulong, Block> blocks, Block currBlock, int pushOpIndex)
        {
            OpCodePush pushOp = currBlock.PushOpCodes[pushOpIndex];

            Stack<PathBlockState> workQueue = new Stack<PathBlockState>();

            HashSet<Block> visited = new HashSet<Block>();

            Stack<ulong> branchStack = new Stack<ulong>();

            void Push(PathBlockState pbs)
            {
                if (pbs.Block == null || visited.Add(pbs.Block))
                {
                    workQueue.Push(pbs);
                }
            }

            Push(new PathBlockState(currBlock));

            while (workQueue.TryPop(out PathBlockState pbs))
            {
                if (pbs.ReturningFromVisit)
                {
                    pbs.RestoreStackState(branchStack);

                    continue;
                }

                Block current = pbs.Block;

                int pushOpsCount = current.PushOpCodes.Count;

                if (pushOpsCount != 0)
                {
                    Push(new PathBlockState(branchStack.Count));

                    for (int index = pushOpIndex; index < pushOpsCount; index++)
                    {
                        branchStack.Push(current.PushOpCodes[index].GetAbsoluteAddress());
                    }
                }

                pushOpIndex = 0;

                if (current.Next != null)
                {
                    Push(new PathBlockState(current.Next));
                }

                if (current.Branch != null)
                {
                    Push(new PathBlockState(current.Branch));
                }
                else if (current.GetLastOp() is OpCodeBranchIndir brIndir)
                {
                    foreach (Block possibleTarget in brIndir.PossibleTargets)
                    {
                        Push(new PathBlockState(possibleTarget));
                    }
                }
                else if (current.GetLastOp() is OpCodeBranchPop op)
                {
                    ulong targetAddress = branchStack.Pop();

                    if (branchStack.Count == 0)
                    {
                        branchStack.Push(targetAddress);

                        op.Targets.Add(pushOp, op.Targets.Count);

                        pushOp.PopOps.TryAdd(op, Local());
                    }
                    else
                    {
                        Push(new PathBlockState(targetAddress));
                        Push(new PathBlockState(blocks[targetAddress]));
                    }
                }
            }
        }
    }
}