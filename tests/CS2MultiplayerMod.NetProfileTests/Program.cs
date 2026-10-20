using System;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

const float limit = 2.5f;
float[] steps = { 0f, 10f, 10f, 10f, 10f };
var breaks = new int[NetWaterProfilePin.MaxPieces + 1];

// A committed edge on a constant grade is one line; a real bend is never flattened.
Check(NetWaterProfilePin.Simplify(new[] { 0f, 1f, 2f, 3f, 4f }, steps, 5,
    NetWaterProfilePin.DryChordTolerance, NetWaterProfilePin.MaxPieces, breaks) == 1,
    "a constant grade should be pinnable");
Check(NetWaterProfilePin.Simplify(new[] { 0f, 1f, 3f, 3f, 4f }, steps, 5,
    NetWaterProfilePin.DryChordTolerance, NetWaterProfilePin.MaxPieces, breaks) > 1,
    "a bent dry road must not be flattened");

// Only a floor and a ceiling together fix the deck; two underground ends follow terrain alone.
Check(NetWaterProfilePin.NeedsPin(0f, 0f, limit, true), "the elevated floor alone does not fix the deck");
Check(NetWaterProfilePin.NeedsPin(0f, 0f, limit, false), "a level edge is regenerated from local surfaces");
Check(!NetWaterProfilePin.NeedsPin(limit, -limit, limit, false), "floor and ceiling already pin the deck");
Check(!NetWaterProfilePin.NeedsPin(-5f, -5f, limit, false), "a tunnel follows replicated terrain only");

// Every interior probe of a long span must be included in that decision.
int count = NetWaterProfilePin.ProbesFor(400f);
Check(count == 101, "a 400 m span needs 101 probes");
var longDeck = new float[count];
var longSteps = new float[count];
for (int i = 1; i < count; i++) longSteps[i] = 4f;
longDeck[80] = 2f;
Check(NetWaterProfilePin.Simplify(longDeck, longSteps, count,
    NetWaterProfilePin.DryChordTolerance, NetWaterProfilePin.MaxPieces, breaks) > 1,
    "a bend beyond the initial buffer must be detected");
Check(NetWaterProfilePin.ProbesFor(100000f) == 0,
    "an oversized span must be refused instead of undersampled");

Console.WriteLine("Net profile regression checks passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
