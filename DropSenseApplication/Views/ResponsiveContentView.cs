// DropSense — Views/ResponsiveContentView.cs
namespace DropSense.Views;

public abstract class ResponsiveContentView : ContentView
{
    double _lastWidth = -1;

    // OnSizeAllocated is called during every layout pass, including the
    // first one — unlike SizeChanged, which can fail to fire on initial
    // layout for a ContentView hosted inside a Grid's "*" column on some
    // platforms. This is why every width-dependent fix looked like it had
    // no effect: the code was correct but was never being invoked.
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        if (width > 0 && Math.Abs(width - _lastWidth) > 0.5)
        {
            _lastWidth = width;
            OnWidthChanged(width);
        }
    }

    protected abstract void OnWidthChanged(double width);
}