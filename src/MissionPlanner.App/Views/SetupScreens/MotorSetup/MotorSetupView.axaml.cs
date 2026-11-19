using Avalonia;
using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

namespace MissionPlanner.App.Views.SetupScreens.MotorSetup;

/// <summary>The motor layout card of Servo Output: frame drawing, motor order wizard and motor direction.</summary>
public sealed partial class MotorSetupView : UserControl
{
    public MotorSetupView()
    {
        InitializeComponent();
    }

    /// <summary>Another tab was chosen: nothing may keep spinning where its STOP button cannot be seen.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is MotorSetupViewModel vm)
            _ = vm.OnHiddenAsync();
    }
}
