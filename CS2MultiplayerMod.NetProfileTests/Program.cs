using System;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

const float limit = 2.5f;
const float maxSlope = 0.2f;
float[] steps = { 0f, 10f, 10f, 10f, 10f };
float[] sourceTerrain = { 0f, 1f, 2f, 3f, 4f };
float[] changedTerrain = { 0f, 3f, 4f, 5f, 4f };
var source = new float[5];
var receiver = new float[5];
var breaks = new int[NetWaterProfilePin.MaxPieces + 1];

// The same native endpoints rebuild into a different deck after terrain changes.
NetWaterProfilePin.PredictDeck(sourceTerrain, sourceTerrain, steps, 5,
    0f, 4f, 0f, 0f, limit, maxSlope, source);
Check(NetWaterProfilePin.Simplify(source, steps, 5,
    NetWaterProfilePin.DryChordTolerance, NetWaterProfilePin.MaxPieces, breaks) == 1,
    "source constant grade should be pinnable");
NetWaterProfilePin.PredictDeck(changedTerrain, changedTerrain, steps, 5,
    0f, 4f, 0f, 0f, limit, maxSlope, receiver);
Check(Math.Abs(receiver[2] - source[2]) > 1f,
    "changed terrain should reproduce the reported collapse without a pin");
NetWaterProfilePin.PredictDeck(changedTerrain, changedTerrain, steps, 5,
    0f, 4f, limit, -limit, limit, maxSlope, receiver);
CheckSame(source, receiver, "a pinned dry grade must survive terrain changes");

// An elevated-only road on a mountain slope also needs a ceiling to stop higher
// receiver terrain from lifting its interior above the source grade.
float[] mountainTerrain = { 0f, 13f, 15f, 16f, 4f };
NetWaterProfilePin.PredictDeck(sourceTerrain, sourceTerrain, steps, 5,
    10f, 14f, 0f, 0f, limit, maxSlope, source, requireElevated: true);
Check(NetWaterProfilePin.NeedsPin(0f, 0f, limit, true, false, false),
    "the elevated floor alone does not fix the deck");
Check(NetWaterProfilePin.Simplify(source, steps, 5,
    NetWaterProfilePin.DryChordTolerance, NetWaterProfilePin.MaxPieces, breaks) == 1,
    "the elevated source grade should be pinnable");
NetWaterProfilePin.PredictDeck(mountainTerrain, mountainTerrain, steps, 5,
    10f, 14f, 0f, 0f, limit, maxSlope, receiver, requireElevated: true);
Check(Math.Abs(receiver[2] - source[2]) > 1f,
    "mountain terrain should lift an unpinned elevated road");
NetWaterProfilePin.PredictDeck(mountainTerrain, mountainTerrain, steps, 5,
    10f, 14f, limit, -limit, limit, maxSlope, receiver, requireElevated: true);
CheckSame(source, receiver, "the elevated grade pin must survive mountain terrain");

// A real bend is never replaced by the straight line between endpoints.
Check(NetWaterProfilePin.Simplify(new[] { 0f, 1f, 3f, 3f, 4f }, steps, 5,
    NetWaterProfilePin.DryChordTolerance, NetWaterProfilePin.MaxPieces, breaks) > 1,
    "a bent dry road must not be flattened");

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

static void CheckSame(float[] expected, float[] actual, string message)
{
    for (int i = 0; i < expected.Length; i++)
        Check(Math.Abs(expected[i] - actual[i]) < 0.001f, message + " at probe " + i);
}
