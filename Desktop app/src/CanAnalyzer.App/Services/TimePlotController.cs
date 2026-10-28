using OxyPlot;

namespace CanAnalyzer.App.Services;

/// <summary>The same navigation gestures for every time plot.</summary>
public static class TimePlotController
{
    public static IPlotController Create()
    {
        var controller = new PlotController();
        controller.UnbindAll();
        var rectangle = new DelegatePlotCommand<OxyMouseDownEventArgs>((view, ctrl, args) =>
            ctrl.AddMouseManipulator(view, new ReverseZoomRectangleManipulator(view), args));
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Left, OxyModifierKeys.None, 1), rectangle);
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Right, OxyModifierKeys.None, 1), PlotCommands.PanAt);
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Left, OxyModifierKeys.None, 2), PlotCommands.ResetAt);
        controller.Bind(new OxyMouseWheelGesture(OxyModifierKeys.None), PlotCommands.ZoomWheel);
        return controller;
    }
}
