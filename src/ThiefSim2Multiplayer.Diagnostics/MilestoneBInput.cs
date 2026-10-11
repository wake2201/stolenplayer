using System;
using System.Globalization;

namespace ThiefSim2Multiplayer.Diagnostics
{
    // Pure request boundary, shared with tests without loading Unity or Harmony.
    internal struct MilestoneBInput
    {
        internal float Seconds, Forward, Right, LookX, LookY;
        internal bool Run;
        internal string Button;
        internal static bool TryParse(string text, int processId, out MilestoneBInput input)
        {
            input = default(MilestoneBInput);
            if (text == null || text.Length > 256 || processId <= 0) return false;
            var args = text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length != 8 || args[7] != processId.ToString(CultureInfo.InvariantCulture)) return false;
            var values = new float[5];
            for (int i = 0; i < values.Length; i++)
                if (!float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || float.IsNaN(values[i]) || float.IsInfinity(values[i])) return false;
            bool run;
            if (!bool.TryParse(args[5], out run) || values[0] < 0.1f || values[0] > 60 ||
                Math.Abs(values[1]) > 1 || Math.Abs(values[2]) > 1 || Math.Abs(values[3]) > 100 || Math.Abs(values[4]) > 100) return false;
            switch (args[6])
            {
                case "none": case "crouch": case "jump": case "use_item": case "interact": case "run": break;
                default: return false;
            }
            input = new MilestoneBInput { Seconds = values[0], Forward = values[1], Right = values[2], LookX = values[3], LookY = values[4], Run = run, Button = args[6] };
            return true;
        }
    }
}
