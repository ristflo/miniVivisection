using System;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// While a Grasshopper script component is paused in its solution (miniVivisection.GH's
    /// PausedSolution does the work, by reflection): what is downstream of it is marked on the
    /// canvas ("While paused: mark what depends on the paused script"), and a change made
    /// meanwhile -- what the pause guard held back -- is computed at once with what is downstream
    /// of it, from the paused component's last data ("While paused: compute changes downstream").
    /// Looked at four times a second on the UI thread (FreePause keeps it going); the guard still
    /// does every held change properly when the solution ends.
    /// </summary>
    internal static class GrasshopperPausedData
    {
        private static UITimer s_timer;
        private static object s_marked;
        private static object s_paused;
        private static int s_doneVersion;

        public static void Start()
        {
            if (s_timer != null)
                return;
            s_timer = new UITimer { Interval = 0.25 };
            s_timer.Elapsed += (s, e) => Tick();
            s_timer.Start();
        }

        private static void Tick()
        {
            try
            {
                var paused = GrasshopperPauseGuard.AnyPaused() ? GrasshopperRuns.RunningComponent : null;
                if (!ReferenceEquals(paused, s_paused))
                {
                    s_paused = paused;
                    s_doneVersion = GrasshopperPauseGuard.HeldVersion; // what was held before this pause isn't this pause's
                }
                var mark = MiniVivisectionSettings.GrasshopperPausedMarks ? paused : null;
                if (!ReferenceEquals(mark, s_marked))
                {
                    s_marked = mark;
                    GrasshopperBridge.MarkPaused(mark);
                }
                if (paused == null || !MiniVivisectionSettings.GrasshopperPausedRecompute || GrasshopperPauseGuard.HeldVersion == s_doneVersion)
                    return;
                s_doneVersion = GrasshopperPauseGuard.HeldVersion;
                GrasshopperPauseGuard.Quietly = true;
                string done;
                try
                {
                    done = GrasshopperBridge.RecomputeWhilePaused(paused, GrasshopperPauseGuard.HeldItems());
                }
                finally
                {
                    GrasshopperPauseGuard.Quietly = false;
                }
                if (done != null)
                {
                    UserLog.Info("Grasshopper, while '{0}' is paused: {1} -- properly again when the pause ends.", GrasshopperRuns.NickName(paused), done);
                    Log.Info(LogArea.Breakpoints, "while paused: {0}", done);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "while paused, the definition's data: {0}", Log.Describe(ex));
            }
        }
    }
}
