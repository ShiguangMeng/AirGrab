namespace AirGrab.Core;

public enum GestureState { Idle, Hover, Grabbing, Carrying, Drop }

public static class GestureStateExtensions
{
    public static string Title(this GestureState state) => state switch
    {
        GestureState.Idle => "待机",
        GestureState.Hover => "悬停",
        GestureState.Grabbing => "抓取",
        GestureState.Carrying => "搬运",
        GestureState.Drop => "投送",
        _ => "待机"
    };

    public static string Code(this GestureState state) => state switch
    {
        GestureState.Idle => "IDLE",
        GestureState.Hover => "HOVER",
        GestureState.Grabbing => "GRABBING",
        GestureState.Carrying => "CARRYING",
        GestureState.Drop => "DROP",
        _ => "IDLE"
    };

    public static string Hint(this GestureState state) => state switch
    {
        GestureState.Idle => "等待手势",
        GestureState.Hover => "手掌已识别",
        GestureState.Grabbing => "握拳确认 0.5s",
        GestureState.Carrying => "移向目标设备",
        GestureState.Drop => "张开手掌释放",
        _ => "等待手势"
    };

    /// <summary>进度指示用序号，Idle 为 0。</summary>
    public static int Step(this GestureState state) => state switch
    {
        GestureState.Idle => 0,
        GestureState.Hover => 1,
        GestureState.Grabbing => 2,
        GestureState.Carrying => 3,
        GestureState.Drop => 4,
        _ => 0
    };
}

/// <summary>
/// 单帧手部姿态。
/// 摄像头通道写入 MediaPipe 关键点的推断值；模拟通道由指针事件合成同一结构，
/// 因此状态机对输入来源完全无感知。
/// </summary>
public readonly record struct HandPose(double Openness, double X, double Y, float Confidence, int HandCount)
{
    public static HandPose None => new(0, 0.5, 0.5, 0, 0);

    public bool IsFist => Openness <= 0.35;
    public bool IsOpenPalm => Openness >= 0.62;
}

public enum MultiHandPolicy { Reject, UseNearest }

/// <summary>
/// 手势状态机：把连续的手部姿态流收敛成离散的抓取 / 投送事件。
///
/// 两道防误触闸门：
/// 1. 握拳必须持续 <see cref="HoldDuration"/> 才被确认为有意识的抓取；
/// 2. 画面中同时出现两只手时整帧作废，宁可要求重试也不猜测意图。
/// 另一道隐含闸门：抓取成立前必须出现过张开手掌，阻止「凭空握拳」触发。
/// </summary>
public sealed class GestureStateMachine
{
    public TimeSpan HoldDuration { get; set; } = TimeSpan.FromMilliseconds(500);
    public MultiHandPolicy MultiHandPolicy { get; set; } = MultiHandPolicy.Reject;

    public GestureState State { get; private set; } = GestureState.Idle;
    public string PayloadName { get; set; } = "—";

    /// <summary>抓取被确认时触发，参数为掌心位置。</summary>
    public event Action<HandPose>? GrabConfirmed;
    /// <summary>投送被确认时触发。</summary>
    public event Action<HandPose>? DropConfirmed;
    /// <summary>抓取或投送被取消时触发。</summary>
    public event Action? Cancelled;
    /// <summary>状态发生变化时触发。</summary>
    public event Action<GestureState>? StateChanged;

    private DateTimeOffset? _fistSince;
    private bool _seenOpenPalm;
    private bool _hasGrabbed;
    private bool _isCarrying;
    private HandPose _lastPose = HandPose.None;

    public void Ingest(HandPose pose, DateTimeOffset now)
    {
        if (pose.Confidence <= 0.25f)
        {
            ResetToIdleIfIdle();
            return;
        }
        if (pose.HandCount > 1 && MultiHandPolicy == MultiHandPolicy.Reject)
        {
            Cancel();
            return;
        }

        _lastPose = pose;

        if (_isCarrying)
        {
            if (pose.IsOpenPalm)
            {
                _isCarrying = false;
                SetState(GestureState.Drop);
                DropConfirmed?.Invoke(pose);
            }
            return;
        }

        if (_hasGrabbed)
        {
            if (pose.IsFist)
            {
                _isCarrying = true;
                SetState(GestureState.Carrying);
            }
            else if (pose.IsOpenPalm)
            {
                Cancel();
            }
            return;
        }

        if (pose.IsFist)
        {
            if (_fistSince is null)
            {
                _fistSince = now;
                SetState(GestureState.Grabbing);
            }
            else if (now - _fistSince.Value >= HoldDuration && _seenOpenPalm)
            {
                _hasGrabbed = true;
                SetState(GestureState.Grabbing);
                GrabConfirmed?.Invoke(pose);
            }
            return;
        }

        _fistSince = null;

        if (pose.IsOpenPalm)
        {
            _seenOpenPalm = true;
            if (State is GestureState.Idle or GestureState.Hover)
            {
                SetState(GestureState.Hover);
            }
        }
        else if (State == GestureState.Grabbing)
        {
            SetState(GestureState.Hover);
        }
    }

    /// <summary>模拟通道在按下即抓取的场景下直接进入搬运。</summary>
    public void BeginCarrying(string name)
    {
        _hasGrabbed = true;
        _isCarrying = true;
        PayloadName = name;
        SetState(GestureState.Carrying);
    }

    public void ConfirmDrop()
    {
        if (!_isCarrying) return;
        _isCarrying = false;
        SetState(GestureState.Drop);
        DropConfirmed?.Invoke(_lastPose);
    }

    public void Cancel()
    {
        bool wasActive = _isCarrying || _hasGrabbed;
        _hasGrabbed = false;
        _isCarrying = false;
        _fistSince = null;
        SetState(GestureState.Idle);
        if (wasActive) Cancelled?.Invoke();
    }

    /// <summary>一次完整投送结束后复位，等待下一轮手势。</summary>
    public void Finish()
    {
        _hasGrabbed = false;
        _isCarrying = false;
        _fistSince = null;
        _seenOpenPalm = false;
        PayloadName = "—";
        SetState(GestureState.Idle);
    }

    private void ResetToIdleIfIdle()
    {
        if (_isCarrying || _hasGrabbed) return;
        _fistSince = null;
        SetState(GestureState.Idle);
    }

    private void SetState(GestureState next)
    {
        if (State == next) return;
        State = next;
        StateChanged?.Invoke(next);
    }
}
