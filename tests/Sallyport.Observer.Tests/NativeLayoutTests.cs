using System.Runtime.InteropServices;
using Sallyport.Observer;

namespace Sallyport.Observer.Tests;

public sealed class NativeLayoutTests
{
    [Fact]
    public void NativeEtwOffsetsMatchTheWindowsAbi()
    {
        Assert.Equal(48, Marshal.SizeOf<EtwNative.WnodeHeader>());
        Assert.Equal(120, Marshal.SizeOf<EtwNative.Properties>());
        Assert.Equal(80, Marshal.SizeOf<EtwNative.EventHeader>());
        Assert.Equal(IntPtr.Size == 8 ? 112 : 104, Marshal.SizeOf<EtwNative.EventRecord>());
        Assert.Equal(16, Marshal.SizeOf<EtwNative.PropertyDescriptor>());
        Assert.Equal(IntPtr.Size == 8 ? 280 : 272, Marshal.SizeOf<EtwNative.LogfileHeader>());
        Assert.Equal(IntPtr.Size == 8 ? 448 : 416, Marshal.SizeOf<EtwNative.Logfile>());
        Assert.Equal(IntPtr.Size == 8 ? 400 : 384, Marshal.OffsetOf<EtwNative.Logfile>(nameof(EtwNative.Logfile.BufferCallback)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 424 : 400, Marshal.OffsetOf<EtwNative.Logfile>(nameof(EtwNative.Logfile.EventRecordCallback)).ToInt32());
    }
}
