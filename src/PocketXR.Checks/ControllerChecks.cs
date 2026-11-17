using System;
using PocketXR.Core;

namespace PocketXR.Checks;

public static class ControllerChecks
{
    public static void Run()
    {
        // The stick: nothing inside the dead zone, then even growth from 0 at its edge to 1 right over.
        var rest = ControllerLogic.Stick(x: 0.1, y: -0.1);
        Check.That(rest.X == 0 && rest.Y == 0, "stick: at rest inside the dead zone");
        var edge = ControllerLogic.Stick(x: 0.18, y: 0);
        Check.That(edge.X == 0 && edge.Y == 0, "stick: the dead zone's edge is still rest");
        var full = ControllerLogic.Stick(x: 1, y: 0);
        Check.That(Check.Near(full.X, 1) && full.Y == 0, "stick: right over is 1");
        var half = ControllerLogic.Stick(x: 0.59, y: 0);
        Check.That(Check.Near(half.X, 0.5), "stick: halfway past the dead zone is a half");
        var corner = ControllerLogic.Stick(x: 1, y: 1);
        Check.That(Check.Near(Math.Sqrt(corner.X * corner.X + corner.Y * corner.Y), 1), "stick: a corner is no faster than 1");
        Check.That(Check.Near(corner.X, corner.Y), "stick: a diagonal keeps its direction");
        var up = ControllerLogic.Stick(x: 0, y: -0.6);
        Check.That(up.X == 0 && up.Y < 0, "stick: the sign is kept");
        var odd = ControllerLogic.Stick(x: double.NaN, y: 0.5);
        Check.That(odd.X == 0 && odd.Y == 0, "stick: a reading that is not a number is rest");

        Check.That(ControllerLogic.Trigger(0.02) == 0, "trigger: at rest");
        Check.That(Check.Near(ControllerLogic.Trigger(1), 1), "trigger: right down is 1");
        Check.That(Check.Near(ControllerLogic.Trigger(0.52), 0.5), "trigger: halfway");

        // Speeds: 40 degrees a second for the Space, 25 for the size.
        var right = new ControllerSnapshot(rightX: 1, rightY: 0);
        var moved = ControllerLogic.Move(right, dt: 0.5);
        Check.That(Check.Near(moved.YawDeg, -20) && moved.PitchDeg == 0, "move: the stick to the right is a negative yaw, 40 degrees a second");
        var upward = ControllerLogic.Move(new ControllerSnapshot(rightX: 0, rightY: 1), dt: 1);
        Check.That(Check.Near(upward.PitchDeg, 40) && upward.YawDeg == 0, "move: the stick up is a positive pitch");
        Check.That(Check.Near(ControllerLogic.Grow(new ControllerSnapshot(rightTrigger: 1), dt: 1), 25), "size: the right trigger makes bigger");
        Check.That(Check.Near(ControllerLogic.Grow(new ControllerSnapshot(leftTrigger: 1), dt: 0.2), -5), "size: the left trigger makes smaller");
        Check.That(ControllerLogic.Grow(new ControllerSnapshot(leftTrigger: 1, rightTrigger: 1), dt: 1) == 0, "size: both triggers cancel");

        // When the controller acts.
        Check.That(!ControllerLogic.Active(mode: ControllerMode.Off, leftStickIn: true), "mode off: never");
        Check.That(!ControllerLogic.Active(mode: ControllerMode.Held, leftStickIn: false), "mode held: not without the left stick");
        Check.That(ControllerLogic.Active(mode: ControllerMode.Held, leftStickIn: true), "mode held: with the left stick in");
        Check.That(ControllerLogic.Active(mode: ControllerMode.Always, leftStickIn: false), "mode always");

        // Buttons act on the press only.
        var press = ControllerLogic.Pressed(before: ControllerButtons.South | ControllerButtons.LeftStick, now: ControllerButtons.South | ControllerButtons.LeftStick | ControllerButtons.East);
        Check.That(press == ControllerButtons.East, "pressed: only what went down");
        Check.That(ControllerLogic.Pressed(before: ControllerButtons.South, now: default).IsEmpty, "pressed: letting go is nothing");

        Check.That(!ControllerLogic.Continuous(new ControllerSnapshot(buttons: ControllerButtons.South | ControllerButtons.LeftStick, rightX: 0.1)), "timer: off with nothing held that lasts");
        Check.That(ControllerLogic.Continuous(new ControllerSnapshot(rightX: 0.5)), "timer: on with the stick over");
        Check.That(ControllerLogic.Continuous(new ControllerSnapshot(leftTrigger: 0.5)), "timer: on with a trigger down");
        Check.That(ControllerLogic.Continuous(new ControllerSnapshot(buttons: ControllerButtons.Up)), "timer: on with the D-pad held");

        // The pointer's round: the Mac's own screen, then each floating screen.
        Check.That(ControllerLogic.NextScreen(current: null, floating: 2, forward: true) == 0, "round: from the Mac to screen 1");
        Check.That(ControllerLogic.NextScreen(current: 0, floating: 2, forward: true) == 1, "round: on to screen 2");
        Check.That(ControllerLogic.NextScreen(current: 1, floating: 2, forward: true) == null, "round: the last wraps to the Mac");
        Check.That(ControllerLogic.NextScreen(current: null, floating: 2, forward: false) == 1, "round: back from the Mac to the last");
        Check.That(ControllerLogic.NextScreen(current: 0, floating: 2, forward: false) == null, "round: back from screen 1 to the Mac");
        Check.That(ControllerLogic.NextScreen(current: null, floating: 0, forward: true) == null, "round: nowhere with no floating screen");
        Check.That(ControllerLogic.NextScreen(current: 7, floating: 2, forward: true) == 0, "round: a screen that is gone counts as the Mac");
        Check.That(ControllerLogic.NextScreen(current: 2, floating: 3, forward: true, includeOwn: false) == 0, "round in Pocket mode: the last wraps to the first");
        Check.That(ControllerLogic.NextScreen(current: 0, floating: 3, forward: false, includeOwn: false) == 2, "round in Pocket mode: back from the first to the last");
        Check.That(ControllerLogic.NextScreen(current: null, floating: 3, forward: false, includeOwn: false) == 2, "round in Pocket mode: from the Mac back to the last");
        Check.That(ControllerLogic.NextScreen(current: 0, floating: 1, forward: true, includeOwn: false) == 0, "round in Pocket mode: one screen stays");

        // A held D-pad direction: at once, then every 0.25 s, and afresh after letting go.
        var again = new ControllerRepeat(every: 0.25);
        Check.That(!again.Fires(held: false, now: 0), "repeat: nothing while up");
        Check.That(again.Fires(held: true, now: 1.0), "repeat: at once on the press");
        Check.That(!again.Fires(held: true, now: 1.1), "repeat: not before 0.25 s");
        Check.That(again.Fires(held: true, now: 1.25), "repeat: again at 0.25 s");
        Check.That(!again.Fires(held: true, now: 1.4), "repeat: not between");
        Check.That(again.Fires(held: true, now: 1.5), "repeat: again at 0.5 s");
        Check.That(again.Fires(held: true, now: 9), "repeat: once after a stall");
        Check.That(!again.Fires(held: true, now: 9.01), "repeat: no burst after a stall");
        Check.That(!again.Fires(held: false, now: 9.02) && again.Fires(held: true, now: 9.03), "repeat: a new press acts at once");
        int count = 0;
        var counter = new ControllerRepeat(every: 0.25);
        for (int i = 0; i < 120; i++)
        {
            if (counter.Fires(held: true, now: (double)i / 60)) count += 1;
        }
        Check.That(count == 8, $"repeat: 8 times in 2 s at 60 readings a second ({count})");

        // The pacer: 60 additions a second come out as 30 writes, and nothing is lost.
        var pacer = new ControllerPacer(perSecond: 30);
        int writes = 0;
        double total = 0.0;
        for (int i = 0; i < 60; i++)
        {
            if (pacer.Add(1, now: 10 + (double)i / 60) is double amount)
            {
                writes += 1;
                total += amount;
            }
        }
        if (pacer.Flush() is double leftOver) total += leftOver;
        Check.That(writes <= 31 && writes >= 29, $"pacer: about 30 writes a second ({writes})");
        Check.That(Check.Near(total, 60), "pacer: every bit arrives");
        Check.That(pacer.Flush() == null, "pacer: nothing left after a flush");
        var idle = new ControllerPacer(perSecond: 30);
        Check.That(idle.Add(0, now: 1) == null, "pacer: nothing to write is no write");

        // Pocket mode's watch.
        var watch = new PocketWatch(lastScreen: 0);
        static PocketWatch.StepResult Step(ref PocketWatch w, double now, bool down = false, bool cover = false, int? screen = 0,
                                           int floating = 2, bool glasses = true)
        {
            return w.Step(now: now, buttonDown: down, pointerOnCover: cover, pointerScreen: screen, floatingScreens: floating,
                          glassesPresent: glasses);
        }
        Check.That(Step(ref watch, 0, screen: 1) == PocketWatch.StepResult.Stay && watch.LastScreen == 1, "pocket: remembers the screen the pointer is on");
        Check.That(Step(ref watch, 0.1, cover: true, screen: null) == PocketWatch.StepResult.PushBack(1), "pocket: the pointer is put back on the screen it was last on");
        Check.That(Step(ref watch, 0.2, down: true, cover: false, screen: null) == PocketWatch.StepResult.Stay, "pocket: a drag is left alone");
        Check.That(Step(ref watch, 0.3, down: true, cover: true, screen: null) == PocketWatch.StepResult.Stay, "pocket: the hold begins");
        Check.That(Check.Near(watch.Progress(now: 0.8), 0.5), "pocket: the ring is half full halfway");
        Check.That(watch.HintShown(now: 0.8), "pocket: the line shows while held");
        Check.That(Step(ref watch, 1.2, down: true, cover: true, screen: null) == PocketWatch.StepResult.Stay, "pocket: not yet at 0.9 s");
        Check.That(Step(ref watch, 1.35, down: true, cover: true, screen: null) == PocketWatch.StepResult.Leave(PocketWatch.Leave.Held), "pocket: leaves after 1 s held");

        var let_go = new PocketWatch();
        _ = Step(ref let_go, 0);
        _ = Step(ref let_go, 1, down: true, cover: true, screen: null);
        Check.That(Step(ref let_go, 1.5, cover: true, screen: null) == PocketWatch.StepResult.PushBack(0) && let_go.Progress(now: 1.5) == 0, "pocket: letting go early starts over");
        Check.That(let_go.HintShown(now: 3) && !let_go.HintShown(now: 6), "pocket: the line lingers a little, then goes");
        _ = Step(ref let_go, 7, down: true, cover: true, screen: null);
        Check.That(Step(ref let_go, 7.9, down: true, cover: true, screen: null) == PocketWatch.StepResult.Stay, "pocket: a new hold counts from its own start");

        // The click that turned Pocket mode on is not a hold.
        var clicked = new PocketWatch();
        Check.That(Step(ref clicked, 0, down: true, cover: true, screen: null) == PocketWatch.StepResult.Stay, "pocket: the first click is waited out");
        Check.That(Step(ref clicked, 2, down: true, cover: true, screen: null) == PocketWatch.StepResult.Stay && clicked.Progress(now: 2) == 0, "pocket: the click that turned it on never leaves");
        Check.That(!clicked.HintShown(now: 2), "pocket: no line for that click");

        var gone = new PocketWatch();
        Check.That(Step(ref gone, 0, floating: 0) == PocketWatch.StepResult.Leave(PocketWatch.Leave.NoScreens), "pocket: leaves when the floating screens go");
        var dark = new PocketWatch();
        Check.That(Step(ref dark, 0, glasses: false) == PocketWatch.StepResult.Stay, "pocket: the glasses gone for a moment is waited out");
        Check.That(Step(ref dark, 4.9, glasses: false) == PocketWatch.StepResult.Stay, "pocket: still waiting at 4.9 s");
        Check.That(Step(ref dark, 5, glasses: false) == PocketWatch.StepResult.Leave(PocketWatch.Leave.NoGlasses), "pocket: leaves after 5 s without the glasses");
        var blink = new PocketWatch();
        _ = Step(ref blink, 0, glasses: false);
        _ = Step(ref blink, 3, glasses: true);
        Check.That(Step(ref blink, 6, glasses: false) == PocketWatch.StepResult.Stay, "pocket: the glasses coming back starts the wait over");
        var fewer = new PocketWatch(lastScreen: 2);
        Check.That(Step(ref fewer, 0, cover: true, screen: null, floating: 2) == PocketWatch.StepResult.PushBack(1), "pocket: a remembered screen that is gone becomes the last one");
    }
}
