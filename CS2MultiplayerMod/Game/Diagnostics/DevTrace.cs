namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// Compile-time gate for per-entity trace lines - the ones written to chase one specific
    /// sync bug, which emit a line per household/property/vehicle and would swamp a player's
    /// log. Mark such a method <c>[Conditional(DevTrace.Symbol)]</c>: without the symbol the
    /// compiler drops the call site and every argument expression, so a shipped build pays
    /// nothing for them and they cannot bit-rot the way deleted code does.
    /// Build with <c>-p:MpDevTrace=true</c> to get them back.
    /// </summary>
    internal static class DevTrace
    {
        public const string Symbol = "MP_DEV_TRACE";
    }
}
