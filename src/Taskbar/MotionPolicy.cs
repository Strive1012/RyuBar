using RyuBar.Interop;

namespace RyuBar.Taskbar;

/// <summary>
/// 纯策略：把连续的几何采样判定成"在动 / 稳定"。
///
/// 为什么需要它：任务栏滑入滑出时会连续移动，如果在移动中跟随重排，
/// 会看到条在追动画、还会反复重设窗口区域。做法是"不跟动画较劲"——
/// 移动期间冻结一切，连续 N 个相同样本才算稳定，稳定后一次性落定。
/// </summary>
public sealed class MotionPolicy
{
    public const int RequiredStableSamples = 2;

    private RECT _last;
    private bool _hasLast;
    private int _stable;

    public bool IsMoving { get; private set; } = true;

    public bool IsStable => !IsMoving;

    /// <summary>喂入一次采样；返回本次是否已达到"稳定"。</summary>
    public bool Observe(in RECT rect)
    {
        if (!_hasLast)
        {
            _last = rect;
            _hasLast = true;
            _stable = 1;
            IsMoving = RequiredStableSamples > 1;
            return IsStable;
        }

        if (rect.SameAs(_last))
        {
            if (_stable < RequiredStableSamples)
            {
                _stable++;
            }
        }
        else
        {
            _last = rect;
            _stable = 1;
        }

        IsMoving = _stable < RequiredStableSamples;
        return IsStable;
    }

    public void Reset()
    {
        _hasLast = false;
        _stable = 0;
        IsMoving = true;
    }
}
