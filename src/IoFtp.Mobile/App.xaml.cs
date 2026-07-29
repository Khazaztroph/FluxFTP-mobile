namespace IoFtp.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new NavigationPage(new MainPage())
        {
            BarBackgroundColor = Color.FromArgb("#18212B"),
            BarTextColor = Color.FromArgb("#E6EDF3")
        };
    }
}
