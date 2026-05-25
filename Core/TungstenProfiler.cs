using Vintagestory.API.Common;
using Vintagestory.Server;

namespace Tungsten
{
    /// <summary>
    /// Lightweight accessor for ServerMain.FrameProfiler.
    /// All methods are no-op if profiler is unavailable or disabled.
    /// </summary>
    public static class TungstenProfiler
    {
        public static void Init() { }

        public static void Mark(string code)
        {
            var p = ServerMain.FrameProfiler;
            if (p != null && p.Enabled)
                p.Mark(code);
        }

        public static void Enter(string code)
        {
            var p = ServerMain.FrameProfiler;
            if (p != null && p.Enabled)
                p.Enter(code);
        }

        public static void Leave()
        {
            var p = ServerMain.FrameProfiler;
            if (p != null && p.Enabled)
                p.Leave();
        }
    }
}
