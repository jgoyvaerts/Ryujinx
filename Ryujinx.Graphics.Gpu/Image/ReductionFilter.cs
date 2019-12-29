namespace Ryujinx.Graphics.Gpu.Image
{
    /// <summary>
    /// Represents a filter used with texture minification linear filtering.
    /// This feature is only supported on NVIDIA GPUs.
    /// </summary>
    enum ReductionFilter
    {
        Average,
        Minimum,
        Maximum
    }
}
