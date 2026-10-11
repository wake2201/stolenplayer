// Project-owned net46 compatibility, compiled only for the game's older Mono BCL.
#if NET46
using System;
namespace LiteNetLib
{
    internal enum OSPlatform { Windows, Linux, OSX }
    internal static class RuntimeInformation
    {
        internal static bool IsOSPlatform(OSPlatform platform)
        {
            PlatformID current = Environment.OSVersion.Platform;
            if (platform == OSPlatform.Windows)
                return current == PlatformID.Win32NT || current == PlatformID.Win32Windows || current == PlatformID.Win32S || current == PlatformID.WinCE;
            if (platform == OSPlatform.OSX) return current == PlatformID.MacOSX;
            return current == PlatformID.Unix;
        }
    }
}
#endif
