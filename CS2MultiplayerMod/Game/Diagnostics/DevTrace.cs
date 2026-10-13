namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// Compile-time gate for per-entity trace lines: mark a method <c>[Conditional(DevTrace.Symbol)]</c>
    /// and shipped builds drop the call and its arguments. Build with <c>-p:MpDevTrace=true</c>.
    /// </summary>
    internal static class DevTrace
    {
        public const string Symbol = "MP_DEV_TRACE";
    }
}
