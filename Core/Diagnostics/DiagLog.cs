using Vintagestory.API.Server;

namespace Tungsten.Diagnostics
{
    public static class DiagLog
    {
        public static void Header(ICoreServerAPI api, IServerPlayer caller, string moduleName)
        {
            var msg = $"=== [Tungsten] Diagnostic: {moduleName} ===";
            api.Logger.Notification(msg);
            caller?.SendMessage(0, msg, Vintagestory.API.Common.EnumChatType.Notification);
        }

        public static void Line(ICoreServerAPI api, IServerPlayer caller, string text)
        {
            var msg = $"[Tungsten/diag] {text}";
            api.Logger.Notification(msg);
            caller?.SendMessage(0, msg, Vintagestory.API.Common.EnumChatType.Notification);
        }

        public static void Footer(ICoreServerAPI api, IServerPlayer caller)
        {
            var msg = "=== end diag ===";
            api.Logger.Notification(msg);
            caller?.SendMessage(0, msg, Vintagestory.API.Common.EnumChatType.Notification);
        }
    }
}
