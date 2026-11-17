using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace PocketXR.Core;

/// <summary>
/// The glasses' own settings, kept right for "window in space" and for Steady mode, as Pocket XR does on the phone
/// and the Deck. Ported from the Deck's settingsctl.py and, for Steady mode, from Pocket XR's SteadyMode and
/// GlassesDisplayControl.
///
/// Runs on its own thread. Only commands Pocket XR has proven are sent (ControlWire.Sendable), one at a time, at least
/// 3 s apart, each read back (on the Deck a Spatial Anchor set sent within a second of Ultrawide was refused).
///
///   On the Aura:  Spatial Anchor off, Ultrawide off (the app does the head tracking), follow mode, then 3D.
///   Steady mode:  2D, Ultrawide on, Ultrawide's largest screen size, then (6DoF on the Aura) Spatial Anchor on, last.
///                 The glasses then hold one flat canvas themselves, so the app draws head locked (State.Steady).
///   Leaving it:   Spatial Anchor off first, Ultrawide off, the screen size and 3D as they were before.
///   Quit:         Steady mode left first, then only what this app changed is put back, then 2 s of waiting.
///
/// Steady mode has been run on the Aura with the phone and the Deck, never yet on a Mac, and never on the One series.
/// </summary>
public sealed class SettingsControl
{
    public record struct State
    {
        public bool Link;
        public int? Stereo;
        public int? Anchor;
        public int? Ultrawide;
        public bool Busy;
        /// <summary>
        /// Steady mode is on and its whole sequence has completed: the glasses hold the canvas, so the app draws head
        /// locked. False again once it has been left (or could not start and was put back).
        /// </summary>
        public bool Steady;
        /// <summary>
        /// The glasses' screen size (10397), where the model has it: read on the Aura, and on the One series once Steady
        /// mode has been used.
        /// </summary>
        public double? SizeFactor;
        /// <summary>The largest screen size of the mode the glasses are in (10396).</summary>
        public double? SizeMax;
        /// <summary>The glasses' space mode (10421): true follow, false hover. Read on the Aura only.</summary>
        public bool? SpaceFollow;
        /// <summary>Something the user has to do in the glasses' own menu for Steady mode (6DoF on the One series), else null.</summary>
        public string? SteadyNote;
    }

    internal const double SetGapS = 3.0;
    internal const double FollowReadS = 1.5;
    internal const double FollowRetryS = 3.0;
    internal const int FollowTries = 3;
    internal const double FollowCheckS = 5.0;
    internal const double FollowSettleS = 3.0;
    internal const int SbsTries = 2;
    internal const double SbsDropWindowS = 20.0;
    internal const int Tries = 2;
    // Steady mode (Pocket XR's GlassesDisplayCommands: STILL_TRIES, ANCHOR_TRIES, STILL_READ_MS).
    internal const int SteadySets = 8;
    internal const int SteadyAnchorTries = 2;
    internal const double SteadyReadS = 1.0;
    /// <summary>
    /// Pocket XR gives a change 25 s on the phone; here a re-plugged display's link is waited for inside the change, so
    /// it gets longer.
    /// </summary>
    internal const double SteadyGiveUpS = 75.0;
    internal const double SteadyLinkWaitS = 30.0;
    /// <summary>How long the screen size is waited for in Ultrawide before Steady mode goes on without changing it.</summary>
    internal const double SteadySizeWaitS = 6.0;

    private enum JobKind
    {
        Apply,
        Restore,
        Steady,
        Recentre,
    }

    /// <summary>Swift's Job: apply, restore(done), steady(on), recentre.</summary>
    private readonly struct Job
    {
        public readonly JobKind Kind;
        public readonly Action? Done;
        public readonly bool On;

        private Job(JobKind kind, Action? done, bool on)
        {
            Kind = kind;
            Done = done;
            On = on;
        }

        public static Job Apply => new(JobKind.Apply, null, false);
        public static Job Restore(Action done) => new(JobKind.Restore, done, false);
        public static Job Steady(bool on) => new(JobKind.Steady, null, on);
        public static Job Recentre => new(JobKind.Recentre, null, false);
    }

    /// <summary>Seconds on a clock that only runs forward: every time here is an interval.</summary>
    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Swift's Date.distantPast: longer ago than any wait.</summary>
    private const double DistantPast = double.NegativeInfinity;

    private static void Sleep(double seconds)
    {
        if (seconds > 0) Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }

    private readonly Glasses glasses;
    private readonly Action<State> onState;
    private readonly object jobsLock = new();
    private readonly List<Job> jobs = new();
    private volatile ControlLink? link;
    private bool stopped = false;
    private State state = new();
    private int followTriesUsed = 0;
    private double lastFollowCheck = DistantPast;
    private double followSentAt = DistantPast;
    private int sbsTriesUsed = 0;
    private double? sbsFirstAt;
    private bool weSet3D = false;
    /// <summary>The last set of any kind, so two are never closer than [SetGapS].</summary>
    private double lastSetAt = DistantPast;
    private double linkOpenedAt = DistantPast;
    // Steady mode. All of it is touched on the settings thread only.
    /// <summary>Steady mode is asked for (being entered, or held).</summary>
    private bool steadyWanted = false;
    /// <summary>Its sequence completed and the glasses hold the canvas.</summary>
    private bool steadyHeld = false;
    /// <summary>The kind it was entered as: coming in the setting decides, going out the kind it was decides.</summary>
    private bool steadySix = false;
    /// <summary>Steady mode was used this run, so these glasses' screen size is read too (the One series is otherwise left alone).</summary>
    private bool steadyUsed = false;
    private SteadyPlan.Before? before;
    private bool weChangedSize = false;
    private double? lastDropAt;
    /// <summary>The glasses refused or never answered the screen size on this link: not asked again, nothing there to change.</summary>
    private bool sizeUnanswered = false;
    private int sizeSilent = 0;

    /// <summary>The app's "3D side by side" setting.</summary>
    public Func<bool> Want3D = () => true;
    /// <summary>The app's "Steady mode in 6DoF" setting. Used only on glasses that can ([Glasses.SixDofCapable]).</summary>
    public Func<bool> SteadySixDof = () => true;

    public SettingsControl(Glasses glasses, Action<State> onState)
    {
        this.glasses = glasses;
        this.onState = onState;
    }

    public void Start()
    {
        var t = new Thread(Run) { Name = "Pocket XR glasses settings", IsBackground = true };
        t.Start(); // each new link applies the settings (Open)
    }

    /// <summary>Puts the settings right again (after the 3D setting changed).</summary>
    public void Apply() => Enqueue(Job.Apply);

    /// <summary>Puts the glasses back for normal use, then calls [done] (on the settings thread).</summary>
    public void Restore(Action done) => Enqueue(Job.Restore(done));

    /// <summary>
    /// Queues entering or leaving Steady mode. State.Steady turns true when the whole sequence has completed, and
    /// false when it has been left. Only on glasses that are offered it ([Glasses.SteadyCapable]); elsewhere nothing
    /// is sent. An earlier request still waiting is replaced.
    /// </summary>
    public void SetSteady(bool on)
    {
        lock (jobsLock)
        {
            jobs.RemoveAll(j => j.Kind == JobKind.Steady);
            jobs.Add(Job.Steady(on));
        }
    }

    /// <summary>
    /// In Steady mode: the glasses' own recentre (10279), which slides their canvas in front of the head. Does nothing
    /// outside Steady mode.
    /// </summary>
    public void RecentreCanvas()
    {
        lock (jobsLock)
        {
            jobs.RemoveAll(j => j.Kind == JobKind.Recentre);
            jobs.Add(Job.Recentre);
        }
    }

    public void Stop()
    {
        lock (jobsLock) stopped = true;
        link?.Close();
    }

    private void Enqueue(Job j)
    {
        lock (jobsLock) jobs.Add(j);
    }

    private Job? NextJob()
    {
        lock (jobsLock)
        {
            if (jobs.Count == 0) return null;
            Job j = jobs[0];
            jobs.RemoveAt(0);
            return j;
        }
    }

    private bool IsStopped { get { lock (jobsLock) return stopped; } }

    private void Run()
    {
        while (!IsStopped)
        {
            if (link == null || link!.Closed)
            {
                if (!Open())
                {
                    Sleep(2);
                    continue;
                }
            }
            if (NextJob() is not Job job)
            {
                if (Now() - lastFollowCheck >= FollowCheckS)
                {
                    if (steadyWanted)
                    {
                        // No follow and no 3D ask while Steady mode is held: either takes the Aura out of Ultrawide.
                        KeepSteady();
                    }
                    else
                    {
                        KeepFollow();
                        Keep3D();
                    }
                }
                Sleep(0.25);
                continue;
            }
            state.Busy = true;
            Publish();
            switch (job.Kind)
            {
                case JobKind.Apply:
                    ApplyWindowInSpace();
                    break;
                case JobKind.Restore:
                    RestoreGlasses();
                    job.Done!();
                    break;
                case JobKind.Steady:
                    if (job.On) EnterSteady(fromUser: true); else LeaveSteady(quitting: false);
                    break;
                case JobKind.Recentre:
                    RecentreNow();
                    break;
            }
            state.Busy = false;
            Publish();
        }
    }

    private bool Open() => OpenLink(queueApply: true);

    /// <summary>
    /// Opens a new control link and reads the settings on it. [queueApply] false inside a Steady mode change, which
    /// carries on by itself on the new link.
    /// </summary>
    private bool OpenLink(bool queueApply)
    {
        ControlLink l;
        try
        {
            l = new ControlLink();
        }
        catch (Exception)
        {
            if (state.Link) { state.Link = false; Publish(); }
            return false;
        }
        link = l;
        linkOpenedAt = Now();
        sizeUnanswered = false;
        sizeSilent = 0;
        Log.P($"settings: control link open via {l.Via}");
        followTriesUsed = 0;
        sbsTriesUsed = 0;
        sbsFirstAt = null;
        Read();
        if (queueApply) Enqueue(Job.Apply);
        return true;
    }

    /// <summary>The open link, opening a new one if the old one closed (a display re-plug closes it), for up to [waitS].</summary>
    private ControlLink? LiveLink(double waitS)
    {
        double end = Now() + waitS;
        while (!IsStopped)
        {
            if (link is ControlLink l && !l.Closed) return l;
            if (OpenLink(queueApply: false)) continue;
            if (Now() >= end) return null;
            Sleep(2);
        }
        return null;
    }

    private State Read()
    {
        if (link is not ControlLink l) return state;
        state.Link = !l.Closed;
        state.Stereo = l.Get(ControlWire.StereoGet)?.Flag;
        state.Anchor = l.Get(ControlWire.AnchorGet)?.Flag;
        if (glasses.Managed || steadyUsed) ReadSize(l);
        // Ultrawide is read last: when it says on, the size range read beside it is already Ultrawide's own.
        state.Ultrawide = l.Get(ControlWire.UltrawideGet)?.Flag;
        Publish();
        return state;
    }

    /// <summary>
    /// The screen size and its range (10397, 10396). Glasses that refuse them, or never answer, are not asked again on
    /// this link: they have nothing here to change, and the rest goes on without it.
    /// </summary>
    private void ReadSize(ControlLink l)
    {
        if (sizeUnanswered)
        {
            state.SizeFactor = null;
            state.SizeMax = null;
            return;
        }
        ControlWire.Answer? range = l.Get(ControlWire.SizeRange);
        ControlWire.Answer? size = l.Get(ControlWire.SizeGet);
        if (!(range is ControlWire.Answer r && size is ControlWire.Answer s))
        {
            state.SizeFactor = null;
            state.SizeMax = null;
            if (!l.Closed)
            {
                sizeSilent += 1;
                if (sizeSilent >= 2)
                {
                    sizeUnanswered = true;
                    Log.P("settings: the glasses do not answer the screen size; it is left alone on this link");
                }
            }
            return;
        }
        sizeSilent = 0;
        if (r.Result != 0 || s.Result != 0)
        {
            sizeUnanswered = true;
            state.SizeFactor = null;
            state.SizeMax = null;
            Log.P($"settings: the glasses refuse the screen size (results {r.Result}, {s.Result}); it is left alone on this link");
            return;
        }
        if (s.Value is double v && double.IsFinite(v)) state.SizeFactor = v; else state.SizeFactor = null;
        state.SizeMax = ControlWire.BelievableSize(r.Value2);
    }

    private void Publish()
    {
        State s = state;
        s.Link = link is ControlLink l ? !l.Closed : false;
        onState(s);
    }

    /// <summary>Waits until [SetGapS] has passed since the last set.</summary>
    private void PaceSet()
    {
        double wait = SetGapS - (Now() - lastSetAt);
        if (wait > 0) Sleep(wait);
    }

    /// <summary>Sets a value, then reads it back until it holds or [SetGapS] has passed; tries twice.</summary>
    private bool SetAndCheck(string what, int get, int want, Func<uint, byte[]> build, int tries = Tries)
    {
        return SetAndHold(what, tries: tries, build, holds: l => l.Get(get)?.Flag == want);
    }

    /// <summary>Sends one set, then asks [holds] until it says yes or [SetGapS] has passed; [tries] times at most.</summary>
    private bool SetAndHold(string what, int tries, Func<uint, byte[]> build, Func<ControlLink, bool> holds)
    {
        if (link is not ControlLink l) return false;
        for (int attempt = 1; attempt <= Math.Max(1, tries); attempt++)
        {
            PaceSet();
            Log.P($"settings: {what} (try {attempt})");
            try { l.Request(what, build); } catch (Exception) { }
            lastSetAt = Now();
            double end = Now() + SetGapS;
            while (Now() < end)
            {
                if (l.Closed)
                {
                    Log.P($"settings: the control link closed after {what} (the display re-plugging?)");
                    return false;
                }
                if (holds(l))
                {
                    // The display may still be re-plugging: the rest of the gap is waited out.
                    Sleep(Math.Max(0, end - Now()));
                    return true;
                }
                Sleep(0.5);
            }
        }
        Log.P($"settings: {what} did not read back");
        return false;
    }

    /// <summary>Sets the glasses' screen size (10398) and reads it back (10397).</summary>
    private bool SetSize(double size, string what)
    {
        if (ControlWire.BelievableSize(size) == null)
        {
            Log.P($"settings: screen size {size.ToString(CultureInfo.InvariantCulture)} is not believed; not sent");
            return false;
        }
        float asFloat = (float)size;
        return SetAndHold(what, tries: 1, id => ControlWire.SizeSetFrame(id, size: asFloat), holds: l =>
        {
            if (!(l.Get(ControlWire.SizeGet) is ControlWire.Answer a && a.Result == 0 && a.Value is double v)) return false;
            return Math.Abs(v - size) <= SteadyPlan.SizeSame;
        });
    }

    private void ApplyWindowInSpace()
    {
        if (steadyWanted)
        {
            // A new link in Steady mode (the glasses came back from a re-plug, perhaps without Ultrawide): Steady mode is
            // put back, and none of the settings for normal tracking below may fight it.
            EnterSteady(fromUser: false);
            return;
        }
        State st = Read();
        if (glasses.Managed)
        {
            // 3DoF: the app tracks the head, so the glasses' own hold must be off.
            if (st.Anchor == 1)
            {
                SetAndCheck("Spatial Anchor off for 3DoF", get: ControlWire.AnchorGet, want: 0, id => ControlWire.AnchorSetFrame(id, on: false));
            }
            if (st.Ultrawide == 1)
            {
                SetAndCheck("Ultrawide off for 3DoF", get: ControlWire.UltrawideGet, want: 0, id => ControlWire.UltrawideSetFrame(id, on: false));
            }
            KeepFollow();
        }
        Depth(fresh: true);
        Read();
    }

    /// <summary>3D side by side for the Space, or 2D if the user turned it off.</summary>
    private void Depth(bool fresh)
    {
        if (!(link != null && !steadyWanted)) return; // Steady mode needs 2D: a 3D request would switch Ultrawide off
        State st = Read();
        if (st.Ultrawide == 1) return; // Ultrawide is the user's; a 3D request would switch it off
        if (!Want3D())
        {
            if (st.Stereo == 1 && weSet3D)
            {
                if (SetAndCheck("2D (3D turned off)", get: ControlWire.StereoGet, want: 0, id => ControlWire.StereoSetFrame(id, sbs: false)))
                {
                    weSet3D = false;
                }
            }
            return;
        }
        if (st.Stereo == 1) return;
        if (sbsTriesUsed >= SbsTries && (!fresh || sbsFirstAt != null)) return; // asked already: the mode is the user's
        double wait = FollowSettleS - (Now() - followSentAt);
        if (wait > 0) Sleep(wait);
        sbsTriesUsed += 1;
        if (sbsFirstAt == null) sbsFirstAt = Now();
        if (SetAndCheck($"3D side by side (ask {sbsTriesUsed})", get: ControlWire.StereoGet, want: 1, id => ControlWire.StereoSetFrame(id, sbs: true)))
        {
            weSet3D = true;
        }
    }

    /// <summary>If the glasses drop 3D straight after taking it (the Aura can, 1 to 5 s after a plug-in), ask once more.</summary>
    private void Keep3D()
    {
        if (steadyWanted) return;
        if (!(sbsFirstAt is double first && Now() - first <= SbsDropWindowS)) return;
        if (sbsTriesUsed < SbsTries && Want3D()) Depth(fresh: false);
    }

    /// <summary>
    /// On the Aura: follow mode, so the glasses do not hold the picture against the app's tracking. Never in Steady
    /// mode or in Ultrawide: a follow takes the Aura out of Ultrawide.
    /// </summary>
    private void KeepFollow()
    {
        lastFollowCheck = Now();
        if (!(glasses.Managed && !steadyWanted && link is ControlLink l && !l.Closed)) return;
        if (!(followTriesUsed < FollowTries && state.Ultrawide != 1)) return;
        int? mode = l.Get(ControlWire.SpaceModeGet)?.Flag;
        NoteSpaceMode(mode);
        if (mode == ControlWire.SpaceFollow) return;
        followTriesUsed += 1;
        PaceSet();
        Log.P($"settings: the glasses are in hover; follow mode (try {followTriesUsed})");
        try { l.Request("follow", id => ControlWire.FollowFrame(id)); } catch (Exception) { }
        followSentAt = Now();
        lastSetAt = followSentAt;
        Sleep(FollowReadS);
        int? after = l.Get(ControlWire.SpaceModeGet)?.Flag;
        NoteSpaceMode(after);
        if (after == ControlWire.SpaceFollow)
        {
            Log.P("settings: follow mode holds");
        }
        else
        {
            Sleep(Math.Max(0, FollowRetryS - FollowReadS));
        }
    }

    private void NoteSpaceMode(int? mode)
    {
        bool? follow = mode is int m ? m == ControlWire.SpaceFollow : null;
        if (follow != state.SpaceFollow)
        {
            state.SpaceFollow = follow;
            Publish();
        }
    }

    // MARK: Steady mode

    /// <summary>
    /// Enters Steady mode, or puts it back on a new link. [fromUser] true for the user's own request, where the
    /// "Steady mode in 6DoF" setting decides the kind again.
    /// </summary>
    private void EnterSteady(bool fromUser)
    {
        if (!glasses.SteadyCapable)
        {
            Log.P($"settings: Steady mode is not offered on {glasses.Name}; nothing sent");
            return;
        }
        steadyUsed = true;
        bool first = !steadyWanted;
        if (first || fromUser) steadySix = SteadySixDof() && glasses.SixDofCapable;
        if (first)
        {
            // What was there before, so leaving puts it back.
            State st = Read();
            before = new SteadyPlan.Before(stereo: st.Stereo, ultrawide: st.Ultrawide, size: st.SizeFactor, our3D: weSet3D);
            lastDropAt = null;
            weChangedSize = false;
            SteadyPlan.Step[] plan = SteadyPlan.Steps(entering: true, sixDof: steadySix, state: st, appAnchor: glasses.AppAnchor);
            string kind = steadySix ? "6DoF" : "3DoF";
            Log.P($"settings: Steady mode on ({kind}) for {glasses.Name}; plan {Names(plan)}");
        }
        steadyWanted = true;
        state.SteadyNote = null;
        if (RunSteady(on: true))
        {
            steadyHeld = true;
            state.Steady = true;
            // On the One series the user turns the glasses' own 6DoF on; Pocket XR has no confirmed command for it.
            if (steadySix && !glasses.AppAnchor) state.SteadyNote = SteadyPlan.TurnOn6DoF;
            State st = Read();
            Log.P($"settings: Steady mode on: done (Ultrawide {Show(st.Ultrawide)}, screen size {Show(st.SizeFactor)}, Spatial Anchor {Show(st.Anchor)})");
        }
        else
        {
            Log.P("settings: Steady mode did not start; putting the glasses back");
            LeaveSteady(quitting: false);
        }
    }

    /// <summary>
    /// Leaves Steady mode: Spatial Anchor off first (6DoF on the Aura), Ultrawide off, then the screen size and 3D as
    /// they were before. [quitting] leaves out follow mode and the usual 3D ask.
    /// </summary>
    private void LeaveSteady(bool quitting)
    {
        if (!(steadyWanted || steadyHeld))
        {
            Log.P("settings: Steady mode is not on; nothing sent");
            return;
        }
        SteadyPlan.Before? was = before;
        State st = Read();
        SteadyPlan.Step[] plan = SteadyPlan.Steps(entering: false, sixDof: steadySix, state: st, appAnchor: glasses.AppAnchor, before: was);
        Log.P($"settings: Steady mode off; plan {Names(plan)}");
        // Ultrawide was on before, with a size of its own: that size goes back while the glasses are still in Ultrawide.
        if (weChangedSize && was is SteadyPlan.Before b1 && b1.Ultrawide == 1 && st.Ultrawide == 1
            && SteadyPlan.SizeToPutBack(before: b1.Size, now: st.SizeFactor) is double old1)
        {
            if (SetSize(old1, $"screen size back to {Fixed2(old1)}, as before Steady mode")) weChangedSize = false;
        }
        bool left = RunSteady(on: false);
        st = Read();
        if (!left || st.Ultrawide == 1)
        {
            Log.P($"settings: Steady mode off: the change did not complete (Ultrawide {Show(st.Ultrawide)})");
            if (st.Ultrawide == 1)
            {
                // The glasses still hold the canvas, so the app goes on drawing for it.
                steadyWanted = true;
                steadyHeld = true;
                state.Steady = true;
                Publish();
                return;
            }
        }
        steadyWanted = false;
        steadyHeld = false;
        state.Steady = false;
        state.SteadyNote = (steadySix && !glasses.AppAnchor && !quitting) ? SteadyPlan.TurnOff6DoF : null;
        if (weChangedSize && was is SteadyPlan.Before b2 && b2.Ultrawide != 1
            && SteadyPlan.SizeToPutBack(before: b2.Size, now: st.SizeFactor) is double old2)
        {
            // Each mode keeps its own size on the Aura, so this is seldom needed.
            SetSize(old2, $"screen size back to {Fixed2(old2)}, as before Steady mode");
        }
        weChangedSize = false;
        before = null;
        // Coming out, follow and 3D are asked for again as on a new plug-in.
        followTriesUsed = 0;
        sbsTriesUsed = 0;
        sbsFirstAt = null;
        if (!quitting)
        {
            if (glasses.Managed) KeepFollow();
            Depth(fresh: true);
        }
        // 3D that was the user's own before Steady mode (not asked for by this app) goes back, and stays theirs.
        if (was is SteadyPlan.Before b3 && b3.Stereo == 1 && !b3.Our3D)
        {
            State now = Read();
            if (now.Stereo != 1 && now.Ultrawide != 1)
            {
                double wait = FollowSettleS - (Now() - followSentAt);
                if (wait > 0) Sleep(wait);
                SetAndCheck("3D side by side, as before Steady mode", get: ControlWire.StereoGet, want: 1, id => ControlWire.StereoSetFrame(id, sbs: true));
            }
            weSet3D = false;
        }
        State end = Read();
        Log.P($"settings: Steady mode off: done (Ultrawide {Show(end.Ultrawide)}, 2D / 3D {Show(end.Stereo)}, Spatial Anchor {Show(end.Anchor)})");
    }

    /// <summary>
    /// One Steady mode change, a set at a time: the settings are read, the next step decided from them
    /// (SteadyPlan.Next), that one set sent and read back, and so on until nothing is left. A step that will not hold
    /// is sent again from the next reading, up to [SteadySets] sets in all. True when the glasses read as wanted.
    /// </summary>
    private bool RunSteady(bool on)
    {
        bool sixDof = steadySix;
        bool appAnchor = glasses.AppAnchor;
        double started = Now();
        int sets = 0;
        int anchorTries = 0;
        double? sizeWaitFrom = null;
        bool sizeSkipLogged = false;
        while (!IsStopped)
        {
            if (LiveLink(waitS: SteadyLinkWaitS) == null)
            {
                Log.P($"settings: Steady mode {(on ? "on" : "off")}: no control link for {(int)SteadyLinkWaitS} s");
                return false;
            }
            State r = Read();
            // Past its tries, Spatial Anchor counts as set the way this change wants it, so the change can finish.
            if (anchorTries >= SteadyAnchorTries) r.Anchor = (on && sixDof) ? 1 : 0;
            if (on && r.Ultrawide == 1 && (r.SizeFactor == null || r.SizeMax == null))
            {
                double since = sizeWaitFrom ?? Now();
                sizeWaitFrom = since;
                if (sizeUnanswered || Now() - since >= SteadySizeWaitS)
                {
                    if (!sizeSkipLogged)
                    {
                        sizeSkipLogged = true;
                        Log.P("settings: the screen size was not read; Steady mode goes on with the size as it is");
                    }
                    r.SizeFactor = 1;
                    r.SizeMax = 1;
                }
            }
            SteadyPlan.Step step = SteadyPlan.Next(on: on, state: r, sixDof: sixDof, appAnchor: appAnchor);
            if (step == SteadyPlan.Step.Done) return true;
            if (Now() - started > SteadyGiveUpS || (step != SteadyPlan.Step.Wait && sets >= SteadySets))
            {
                Log.P($"settings: Steady mode {(on ? "on" : "off")}: gave up at step {step.RawValue()} after {sets} sets");
                return false;
            }
            if (step == SteadyPlan.Step.Wait)
            {
                Sleep(SteadyReadS);
                continue;
            }
            sets += 1;
            switch (step)
            {
                case SteadyPlan.Step.AnchorOff:
                    anchorTries += 1;
                    SetAndCheck("Spatial Anchor off (10405 = 0)", get: ControlWire.AnchorGet, want: 0, id => ControlWire.AnchorSetFrame(id, on: false), tries: 1);
                    break;
                case SteadyPlan.Step.AnchorOn:
                    anchorTries += 1;
                    SetAndCheck("Spatial Anchor on (10405 = 1), Steady mode in 6DoF", get: ControlWire.AnchorGet, want: 1, id => ControlWire.AnchorSetFrame(id, on: true), tries: 1);
                    break;
                case SteadyPlan.Step.Leave3D:
                    if (SetAndCheck("2D (10274 = 0), on the way to Ultrawide", get: ControlWire.StereoGet, want: 0, id => ControlWire.StereoSetFrame(id, sbs: false), tries: 1))
                    {
                        weSet3D = false;
                    }
                    break;
                case SteadyPlan.Step.UltrawideOn:
                    SetAndCheck("Ultrawide on (10278 = 1)", get: ControlWire.UltrawideGet, want: 1, id => ControlWire.UltrawideSetFrame(id, on: true), tries: 1);
                    break;
                case SteadyPlan.Step.UltrawideOff:
                    SetAndCheck("Ultrawide off (10278 = 0)", get: ControlWire.UltrawideGet, want: 0, id => ControlWire.UltrawideSetFrame(id, on: false), tries: 1);
                    break;
                case SteadyPlan.Step.ScreenMax:
                    if (r.SizeMax is double top)
                    {
                        if (SetSize(top, $"screen size {Fixed2(top)}, Ultrawide's largest (10398)")) weChangedSize = true;
                    }
                    break;
                case SteadyPlan.Step.Wait:
                case SteadyPlan.Step.Done:
                case SteadyPlan.Step.SizeBack:
                case SteadyPlan.Step.StereoBack:
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// Every few seconds in Steady mode: Ultrawide gone off without Pocket XR asking (the glasses' button pressed
    /// twice, or a re-plug) is put back once, and a second time within 30 s ends Steady mode properly, as the user
    /// then wants out (SteadyMode.dropped).
    /// </summary>
    private void KeepSteady()
    {
        lastFollowCheck = Now();
        if (!(steadyHeld && link is ControlLink l && !l.Closed)) return;
        if (l.Get(ControlWire.UltrawideGet)?.Flag is not int uw) return;
        if (uw == 1) return;
        state.Ultrawide = uw;
        double now = Now();
        SteadyPlanDropped what = SteadyPlan.Dropped(nowS: now, lastDropS: lastDropAt, linkOpenedS: linkOpenedAt);
        // A re-plug is not the user's button, so it does not count toward leaving.
        if (what != SteadyPlanDropped.Replug) lastDropAt = now;
        state.Busy = true;
        Publish();
        switch (what)
        {
            case SteadyPlanDropped.Replug:
                Log.P("settings: Steady mode: the glasses came back from a re-plug without Ultrawide; putting Steady mode back");
                EnterSteady(fromUser: false);
                break;
            case SteadyPlanDropped.BackIn:
                Log.P("settings: Steady mode: the glasses left Ultrawide by themselves (their button?); putting it back");
                EnterSteady(fromUser: false);
                break;
            case SteadyPlanDropped.Leave:
                Log.P($"settings: Steady mode: the glasses left Ultrawide again within {(int)SteadyPlan.SecondDropS} s; ending Steady mode properly");
                LeaveSteady(quitting: false);
                break;
        }
        state.Busy = false;
        Publish();
    }

    /// <summary>The glasses' own recentre of their Ultrawide canvas. It has no value to read back; its answer is logged.</summary>
    private void RecentreNow()
    {
        if (!(steadyHeld && link is ControlLink l && !l.Closed))
        {
            Log.P("settings: the canvas recentre is for Steady mode only; nothing sent");
            return;
        }
        if (!(l.Get(ControlWire.UltrawideGet)?.Flag == 1))
        {
            Log.P("settings: the glasses are not in Ultrawide; no canvas recentre sent");
            return;
        }
        PaceSet();
        Log.P("settings: recentre (10279), the Ultrawide canvas");
        try { l.Request("recentre the canvas", id => ControlWire.RecenterFrame(id)); } catch (Exception) { }
        lastSetAt = Now();
    }

    private static string Names(SteadyPlan.Step[] steps)
    {
        string[] words = steps.Select(s => s.RawValue()).ToArray();
        return words.Length == 0 ? "nothing to do" : string.Join(", ", words);
    }

    private static string Show(int? v) => v is int x ? x.ToString(CultureInfo.InvariantCulture) : "not read";
    private static string Show(double? v) => v is double x ? Fixed2(x) : "not read";

    /// <summary>Swift's String(format: "%.2f").</summary>
    private static string Fixed2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private void RestoreGlasses()
    {
        if (!(link is ControlLink first && !first.Closed)) return;
        bool changed = false;
        if (steadyWanted || steadyHeld)
        {
            LeaveSteady(quitting: true);
            changed = true;
        }
        if (!(link is ControlLink l && !l.Closed)) return;
        State st = Read();
        if (st.Stereo == 1 && weSet3D)
        {
            SetAndCheck("2D", get: ControlWire.StereoGet, want: 0, id => ControlWire.StereoSetFrame(id, sbs: false));
            changed = true;
        }
        weSet3D = false;
        Read();
        // Held a moment after a change, as Pocket XR does, so the glasses keep it.
        if (changed) Sleep(2);
    }
}

/// <summary>
/// What to do when the glasses leave Ultrawide by themselves in Steady mode (SteadyMode.dropped). (SteadyPlan.Dropped
/// on the Mac, an enum inside SteadyPlan; C# cannot have a type and a method of one name in a class, so it stands
/// beside it.)
/// </summary>
public enum SteadyPlanDropped
{
    BackIn,
    Leave,
    Replug,
}

public static class SteadyPlanStepExt
{
    public static string RawValue(this SteadyPlan.Step value) => value switch
    {
        SteadyPlan.Step.Wait => "wait",
        SteadyPlan.Step.AnchorOff => "anchorOff",
        SteadyPlan.Step.AnchorOn => "anchorOn",
        SteadyPlan.Step.Leave3D => "leave3D",
        SteadyPlan.Step.UltrawideOn => "ultrawideOn",
        SteadyPlan.Step.ScreenMax => "screenMax",
        SteadyPlan.Step.UltrawideOff => "ultrawideOff",
        SteadyPlan.Step.SizeBack => "sizeBack",
        SteadyPlan.Step.StereoBack => "stereoBack",
        _ => "done",
    };

    public static SteadyPlan.Step? FromRawValue(string? raw) => raw switch
    {
        "wait" => SteadyPlan.Step.Wait,
        "anchorOff" => SteadyPlan.Step.AnchorOff,
        "anchorOn" => SteadyPlan.Step.AnchorOn,
        "leave3D" => SteadyPlan.Step.Leave3D,
        "ultrawideOn" => SteadyPlan.Step.UltrawideOn,
        "screenMax" => SteadyPlan.Step.ScreenMax,
        "ultrawideOff" => SteadyPlan.Step.UltrawideOff,
        "sizeBack" => SteadyPlan.Step.SizeBack,
        "stereoBack" => SteadyPlan.Step.StereoBack,
        "done" => SteadyPlan.Step.Done,
        _ => null,
    };
}

/// <summary>
/// Steady mode's steps as pure functions, ported from Pocket XR's SteadyMode: what to send next from what the glasses
/// last answered. Nothing here touches a link.
/// </summary>
public static class SteadyPlan
{
    public enum Step
    {
        /// <summary>A setting the step depends on is not read yet.</summary>
        Wait,
        AnchorOff,
        AnchorOn,
        Leave3D,
        UltrawideOn,
        ScreenMax,
        UltrawideOff,
        /// <summary>Putting back what was there before Steady mode (only in [Steps], with its `before`).</summary>
        SizeBack,
        StereoBack,
        Done,
    }

    /// <summary>What the glasses read before Steady mode was entered, so leaving can put it back.</summary>
    public record struct Before
    {
        public int? Stereo;
        public int? Ultrawide;
        public double? Size;
        /// <summary>The 3D that was on had been asked for by this app (so it is the app's to ask for again, not the user's).</summary>
        public bool Our3D;

        public Before(int? stereo = null, int? ultrawide = null, double? size = null, bool our3D = false)
        {
            Stereo = stereo;
            Ultrawide = ultrawide;
            Size = size;
            Our3D = our3D;
        }
    }

    /// <summary>Two screen sizes closer than this are the same.</summary>
    public const double SizeSame = 0.005;
    /// <summary>Ultrawide leaving by itself a second time within this long means the user wants out.</summary>
    public const double SecondDropS = 30.0;
    /// <summary>Ultrawide seen off within this long of a new link is the glasses coming back from a re-plug, not their button.</summary>
    public const double ReplugS = 15.0;

    public const string TurnOn6DoF = "Steady mode in 6DoF: turn on 6DoF in your glasses' menu (Spatial Display) so the screen stays put as you move";
    public const string TurnOff6DoF = "Steady mode is off: turn 6DoF off again in your glasses' menu, and use Follow mode with Stabilizer off";

    /// <summary>
    /// The one thing to do next (SteadyMode.next). Ultrawide can only be turned on from 2D. With [sixDof] the glasses'
    /// Spatial Anchor is turned on last, after Ultrawide and the screen size (each of those re-plugs the display), and
    /// off first on the way out. With [sixDof] on glasses where the user sets 6DoF ([appAnchor] false, the One series
    /// with the Eye) the anchor is neither read nor set.
    /// </summary>
    public static Step Next(bool on, SettingsControl.State state, bool sixDof = false, bool appAnchor = true)
    {
        SettingsControl.State g = state;
        if (g.Ultrawide is not int ultrawide) return Step.Wait;
        if (sixDof && !appAnchor)
        {
            if (!on) return ultrawide == 1 ? Step.UltrawideOff : Step.Done;
            if (g.Stereo is not int stereo1) return Step.Wait;
            if (stereo1 == 1) return Step.Leave3D;
            if (ultrawide != 1) return Step.UltrawideOn;
            if (!(g.SizeFactor is double size1 && g.SizeMax is double top1)) return Step.Wait;
            return size1 < top1 - SizeSame ? Step.ScreenMax : Step.Done;
        }
        if (!on)
        {
            if (sixDof && g.Anchor == 1) return Step.AnchorOff;
            return ultrawide == 1 ? Step.UltrawideOff : Step.Done;
        }
        if (g.Anchor is not int anchor) return Step.Wait;
        if (g.Stereo is not int stereo) return Step.Wait;
        if (!sixDof && anchor == 1) return Step.AnchorOff;
        if (stereo == 1) return Step.Leave3D;
        if (ultrawide != 1) return Step.UltrawideOn;
        if (!(g.SizeFactor is double size && g.SizeMax is double top)) return Step.Wait;
        if (size < top - SizeSame) return Step.ScreenMax;
        if (sixDof && anchor != 1) return Step.AnchorOn;
        return Step.Done;
    }

    /// <summary>
    /// The whole change in order, from [state], taking each step as done once sent: for the log and the checks.
    /// SettingsControl itself decides each step afresh from a new reading ([Next]). The list ends with `Wait` when
    /// a setting it needs is not read yet. Leaving with [before] adds what is put back: the screen size (sent only if
    /// it then reads different) and 3D that was the user's own.
    /// </summary>
    public static Step[] Steps(bool entering, bool sixDof, SettingsControl.State state, bool appAnchor = true, Before? before = null)
    {
        SettingsControl.State s = state;
        var @out = new List<Step>();
        for (int pass = 0; pass < 12; pass++)
        {
            Step step = Next(on: entering, state: s, sixDof: sixDof, appAnchor: appAnchor);
            if (step == Step.Done) break;
            if (step == Step.Wait)
            {
                @out.Add(Step.Wait);
                return @out.ToArray();
            }
            if (step == Step.UltrawideOff && before is Before b && b.Ultrawide == 1
                && SizeToPutBack(before: b.Size, now: s.SizeFactor) != null)
            {
                @out.Add(Step.SizeBack);
                s.SizeFactor = b.Size;
            }
            @out.Add(step);
            switch (step)
            {
                case Step.AnchorOff: s.Anchor = 0; break;
                case Step.AnchorOn: s.Anchor = 1; break;
                case Step.Leave3D: s.Stereo = 0; break;
                case Step.UltrawideOn: s.Ultrawide = 1; break;
                case Step.UltrawideOff: s.Ultrawide = 0; break;
                case Step.ScreenMax: s.SizeFactor = s.SizeMax; break;
                case Step.Wait:
                case Step.Done:
                case Step.SizeBack:
                case Step.StereoBack:
                    break;
            }
        }
        if (!entering && before is Before was)
        {
            if (was.Ultrawide != 1 && SizeToPutBack(before: was.Size, now: s.SizeFactor) != null) @out.Add(Step.SizeBack);
            if (was.Stereo == 1) @out.Add(Step.StereoBack);
        }
        return @out.ToArray();
    }

    /// <summary>
    /// The screen size to set again when leaving, or null when it reads as before, or was never read, or cannot be
    /// believed.
    /// </summary>
    public static double? SizeToPutBack(double? before, double? now)
    {
        if (!(ControlWire.BelievableSize(before) is double old && now is double cur && double.IsFinite(cur))) return null;
        return Math.Abs(cur - old) > SizeSame ? old : null;
    }

    public static SteadyPlanDropped Dropped(double nowS, double? lastDropS, double? linkOpenedS = null)
    {
        if (linkOpenedS is double opened && nowS - opened >= 0 && nowS - opened < ReplugS) return SteadyPlanDropped.Replug;
        if (lastDropS is double last && nowS - last >= 0 && nowS - last < SecondDropS) return SteadyPlanDropped.Leave;
        return SteadyPlanDropped.BackIn;
    }
}
