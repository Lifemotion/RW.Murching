namespace Bwl.Murching.Runtime;

/// <summary>Where inference should run.</summary>
public enum ComputeDevice
{
    /// <summary>Use CUDA when the GPU and its libraries are available, otherwise CPU.</summary>
    Auto,
    Cuda,
    Cpu,
}
