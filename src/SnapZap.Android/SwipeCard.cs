using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;

namespace SnapZap.Droid;

/// <summary>
/// The review queue's card gesture: the photo tracks your finger, a stamp names the action before
/// you commit to it, and letting go either completes the action or springs the card back.
/// </summary>
/// <remarks>
/// <para><b>What this replaces.</b> The first version measured the swipe on <c>ACTION_UP</c> and
/// did nothing at all in between — no movement, no label, no way to tell whether you had passed the
/// threshold or which of the three actions you were about to fire. On a screen whose down-swipe
/// deletes a photo, a gesture with no preview and no abort is the wrong shape entirely.</para>
///
/// <para><b>What is borrowed from dating apps, and what deliberately is not.</b> Tinder/Bumble/Hinge
/// all do the same four things, and all four are worth having: the card follows the finger 1:1, it
/// rotates slightly so it reads as a physical object being thrown, a direction-keyed stamp fades in
/// proportionally to how far you have dragged, and there is a distance threshold with a spring-back
/// below it. Those are copied.</para>
///
/// <para>What is <i>not</i> copied is the mapping. A dating app's left and right are a symmetric
/// binary on one object — reject or accept, both consume the card, both move forward. This screen's
/// three directions are not symmetric at all:</para>
/// <list type="bullet">
///   <item><description><b>Left — keep and advance.</b> The only one that behaves like a dating-app
///   swipe: a decision is made and the card is consumed. It gets the full treatment, rotation and
///   all.</description></item>
///   <item><description><b>Right — go back.</b> Navigation, not a decision. Rendering this as an
///   equal-and-opposite fling would state something false: in card-swipe grammar a card leaving the
///   deck means "that one is settled", and this settles nothing. So right gets
///   <see cref="BackResistance"/> rubber-banding and no rotation — the deck feels tethered, which is
///   the iOS back-swipe grammar rather than the disposal grammar.</description></item>
///   <item><description><b>Down — move to the trash.</b> The destructive one. Dating apps put their
///   consequential action behind a <i>higher</i> bar than the routine one (Tinder's super-like is
///   the rare vertical gesture, not the common horizontal), and that is followed here:
///   <see cref="DeleteThresholdDp"/> is well past the horizontal threshold, so a delete cannot fall
///   out of a sloppy version of a keep.</description></item>
/// </list>
///
/// <para>The stamps carry the rest of the distinction — the trash stamp is the only one in accent
/// red, and each says what it does in words rather than relying on a symbol the user has to have
/// learned. Crossing a threshold fires a haptic tick, which is what tells you the gesture has armed
/// without your having to watch the stamp.</para>
///
/// <para>An upward drag is tracked with heavy resistance and bound to nothing. It has no meaning
/// here, and a gesture that visibly refuses to go anywhere teaches that faster than one that
/// silently ignores you.</para>
/// </remarks>
public sealed class SwipeCard : Java.Lang.Object, View.IOnTouchListener
{
    /// <summary>How far a rightward drag actually moves the card, as a fraction of the finger.</summary>
    const float BackResistance = 0.45f;

    /// <summary>Upward drags go nowhere, and feel like it.</summary>
    const float UpResistance = 0.2f;

    const float KeepThresholdDp = 96f;
    const float DeleteThresholdDp = 168f;

    /// <summary>Movement before an axis is chosen. Once chosen it is locked for the gesture, so a
    /// wandering finger cannot flicker the stamp between two actions.</summary>
    const float DeadZoneDp = 14f;

    /// <summary>Stamps hit full opacity slightly before the threshold, so "armed" is legible.</summary>
    const float StampLead = 0.8f;

    const float MaxRotation = 14f;

    enum Axis { None, Horizontal, Vertical }

    readonly FrameLayout _card;
    readonly Action _onKeep, _onBack, _onDelete;
    readonly Func<bool> _canGoBack;
    readonly TextView _keepStamp, _backStamp, _trashStamp;
    readonly float _keepThreshold, _deleteThreshold, _deadZone;

    float _downX, _downY;
    Axis _axis;
    bool _engaged, _hapticFired, _spent;

    /// <summary>This gesture was handed back to the enclosing ScrollView; stay out of it.</summary>
    bool _yielded;

    /// <param name="card">
    /// The bordered container, not the ImageView inside it — the whole card moves, accent frame
    /// included. The stamps are added as its children so they travel with it.
    /// </param>
    /// <param name="canGoBack">
    /// Whether there is a previous group to go back to. Without this the first card in the queue
    /// would fly off to the right and then reappear centred a frame later, because the underlying
    /// index cannot decrease — a flash that reads as a glitch rather than as "nothing behind this".
    /// </param>
    public SwipeCard(Context c, FrameLayout card, Action onKeep, Action onBack, Action onDelete,
                     Func<bool> canGoBack)
    {
        _card = card;
        _onKeep = onKeep;
        _onBack = onBack;
        _onDelete = onDelete;
        _canGoBack = canGoBack;

        _keepThreshold = Design.Dp(c, KeepThresholdDp);
        _deleteThreshold = Design.Dp(c, DeleteThresholdDp);
        _deadZone = Design.Dp(c, DeadZoneDp);

        // Each stamp sits on the edge *opposite* its direction of travel, which is the detail that
        // makes this readable rather than merely animated: the leading edge of a dragged card is
        // already off-screen, so a stamp pinned there is clipped away exactly when it matters. Same
        // reason Tinder puts LIKE on the left edge and NOPE on the right.
        //
        // The text names the action, not the state — the card already carries a permanent accent
        // tag reading "Keeping this one", and a drag stamp repeating it would say nothing about
        // what letting go is going to do.
        _keepStamp = Stamp(c, "✓ KEEP · NEXT GROUP", Design.Ink, Design.Bg, -11f,
                           GravityFlags.Top | GravityFlags.Right);
        _backStamp = Stamp(c, "◀ PREVIOUS", Design.Surface, Design.Text, 0f,
                           GravityFlags.Top | GravityFlags.Left);
        // The only stamp in accent, because it is the only one that removes a file. Pinned to the
        // top, the trailing edge of a downward drag.
        _trashStamp = Stamp(c, "MOVE TO TRASH", Design.Accent, Color.White, 0f,
                            GravityFlags.Top | GravityFlags.CenterHorizontal);

        card.AddView(_keepStamp);
        card.AddView(_backStamp);
        card.AddView(_trashStamp);

        // No outline provider means no drop shadow when the card is lifted below. This system has
        // no shadows on anything (see Design.Button killing Material's elevation lift), and the Z
        // change here is purely about draw order, not depth.
        card.OutlineProvider = null;

        card.SetOnTouchListener(this);
    }

    /// <summary>
    /// Raises the card above its siblings for the duration of the gesture.
    /// </summary>
    /// <remarks>
    /// The card is one child of a vertical LinearLayout, so by default the metadata rows beneath it
    /// are drawn afterwards and therefore <i>on top</i> — a downward drag slid the photo behind its
    /// own resolution and file-size rows, which read as the card being translucent. Z is the right
    /// lever rather than <c>BringToFront</c>, which in a LinearLayout would reorder the stack and
    /// move the card to the bottom of the screen.
    /// </remarks>
    void Lift(bool lifted) => _card.TranslationZ = lifted ? Design.Dp(_card.Context!, 8) : 0f;

    static TextView Stamp(Context c, string text, Color fill, Color textColor, float rotation, GravityFlags gravity)
    {
        var t = new TextView(c) { Text = text };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, 12.5f);
        t.SetTypeface(null, TypefaceStyle.Bold);
        t.LetterSpacing = 0.1f;
        t.SetTextColor(textColor);
        t.Background = Design.Fill(fill);            // square, like everything else in this system
        t.SetPadding(Design.Dp(c, 10), Design.Dp(c, 6), Design.Dp(c, 10), Design.Dp(c, 6));
        t.Alpha = 0f;
        t.Rotation = rotation;
        t.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = gravity,
            TopMargin = Design.Dp(c, 14),
            BottomMargin = Design.Dp(c, 14),
            LeftMargin = Design.Dp(c, 14),
            RightMargin = Design.Dp(c, 14),
        };
        // Transient drag feedback. The card's own ContentDescription names all three gestures, and
        // a screen reader user is not dragging — announcing these would be noise.
        t.ImportantForAccessibility = ImportantForAccessibility.No;
        return t;
    }

    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (e is null || _spent) return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _card.Animate()?.Cancel();
                _downX = e.RawX;
                _downY = e.RawY;
                _axis = Axis.None;
                _engaged = false;
                _hapticFired = false;
                _yielded = false;
                Lift(true);
                // Claimed here, before any movement, and not once the axis is known. The enclosing
                // ScrollView intercepts at its own touch slop (~8dp), which is *smaller* than this
                // gesture's dead zone — so by the time an axis could be chosen the scroll has
                // already been taken and the card is receiving ACTION_CANCEL. That is why the
                // downward delete swipe did nothing at all: the page scrolled instead.
                _card.Parent?.RequestDisallowInterceptTouchEvent(true);
                return true;

            case MotionEventActions.Move:
            {
                if (_yielded) return false;

                var dx = e.RawX - _downX;
                var dy = e.RawY - _downY;

                if (_axis == Axis.None)
                {
                    if (Math.Abs(dx) < _deadZone && Math.Abs(dy) < _deadZone) return true;

                    if (Math.Abs(dy) > Math.Abs(dx) && dy < 0)
                    {
                        // Upward is bound to nothing here, and claiming every gesture on a card
                        // this tall would leave a big dead patch you cannot scroll the page from.
                        // Hand it straight back to the ScrollView.
                        _yielded = true;
                        Lift(false);
                        _card.Parent?.RequestDisallowInterceptTouchEvent(false);
                        return false;
                    }

                    _axis = Math.Abs(dx) >= Math.Abs(dy) ? Axis.Horizontal : Axis.Vertical;
                    _engaged = true;
                }

                Track(dx, dy);
                return true;
            }

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _card.Parent?.RequestDisallowInterceptTouchEvent(false);
                if (_yielded) return false;
                if (!_engaged) { SpringBack(); return true; }
                Release(e.RawX - _downX, e.RawY - _downY,
                        e.ActionMasked == MotionEventActions.Cancel);
                return true;
        }
        return false;
    }

    void Track(float dx, float dy)
    {
        if (_axis == Axis.Horizontal)
        {
            _card.TranslationY = 0f;
            if (dx < 0)
            {
                // The disposal gesture: 1:1 with the finger, rotating about its own centre.
                _card.TranslationX = dx;
                _card.Rotation = Math.Max(-MaxRotation, dx / Math.Max(1, _card.Width) * MaxRotation * 2f);
                Stamps(Progress(-dx, _keepThreshold), 0f, 0f);
                Haptic(-dx >= _keepThreshold);
            }
            else
            {
                // The navigation gesture: tethered, unrotated, visibly a different kind of thing.
                _card.TranslationX = dx * BackResistance;
                _card.Rotation = 0f;
                Stamps(0f, Progress(dx, _keepThreshold), 0f);
                Haptic(dx >= _keepThreshold);
            }
        }
        else
        {
            _card.TranslationX = 0f;
            _card.Rotation = 0f;
            if (dy > 0)
            {
                _card.TranslationY = dy;
                Stamps(0f, 0f, Progress(dy, _deleteThreshold));
                Haptic(dy >= _deleteThreshold);
            }
            else
            {
                _card.TranslationY = dy * UpResistance;
                Stamps(0f, 0f, 0f);
            }
        }
    }

    static float Progress(float distance, float threshold) =>
        Math.Clamp(distance / (threshold * StampLead), 0f, 1f);

    void Stamps(float keep, float back, float trash)
    {
        _keepStamp.Alpha = keep;
        _backStamp.Alpha = back;
        _trashStamp.Alpha = trash;
    }

    /// <summary>One tick as the gesture arms, and re-arms if the finger comes back under it.</summary>
    void Haptic(bool past)
    {
        if (past && !_hapticFired)
        {
            _card.PerformHapticFeedback(FeedbackConstants.ContextClick);
            _hapticFired = true;
        }
        else if (!past)
        {
            _hapticFired = false;
        }
    }

    void Release(float dx, float dy, bool cancelled)
    {
        if (cancelled) { SpringBack(); return; }

        if (_axis == Axis.Horizontal)
        {
            if (dx <= -_keepThreshold)
            {
                Commit(-_card.Width * 1.6f, 0f, -MaxRotation * 1.6f, 0f, _onKeep);
                return;
            }
            if (dx >= _keepThreshold && _canGoBack())
            {
                // Slides aside rather than away, and keeps most of its opacity: this card is not
                // being disposed of, it is stepping back behind the previous one.
                Commit(_card.Width * 1.1f, 0f, 0f, 0.3f, _onBack);
                return;
            }
        }
        else if (dy >= _deleteThreshold)
        {
            Commit(0f, _card.Height * 1.3f, 0f, 0f, _onDelete);
            return;
        }

        SpringBack();
    }

    /// <summary>
    /// Throws the card and fires the action once it is gone. <see cref="_spent"/> latches so a
    /// second gesture cannot land on a card that is already mid-flight and fire the action twice.
    /// </summary>
    void Commit(float tx, float ty, float rotation, float endAlpha, Action then)
    {
        _spent = true;
        _card.Animate()!
            .TranslationX(tx).TranslationY(ty).Rotation(rotation).Alpha(endAlpha)
            .SetDuration(210)
            .SetInterpolator(new AccelerateInterpolator(1.3f))
            .WithEndAction(new Java.Lang.Runnable(then))!
            .Start();
    }

    void SpringBack()
    {
        _card.Animate()!
            .TranslationX(0f).TranslationY(0f).Rotation(0f).Alpha(1f)
            .SetDuration(220)
            .SetInterpolator(new OvershootInterpolator(1.4f))
            .Start();

        foreach (var s in new[] { _keepStamp, _backStamp, _trashStamp })
            s.Animate()!.Alpha(0f).SetDuration(140).Start();

        // Dropped only once the card is home, so it does not pass behind the metadata rows on the
        // way back.
        _card.PostDelayed(() => { if (!_spent) Lift(false); }, 240);

        _axis = Axis.None;
        _engaged = false;
        _hapticFired = false;
    }
}
