namespace SemanticDiff.Controls.Uno.Sample;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window ??= new Window();
        window.Content ??= new MainPage();
        window.Activate();
    }
}
