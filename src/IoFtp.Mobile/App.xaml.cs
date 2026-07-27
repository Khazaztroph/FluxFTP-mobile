namespace IoFtp.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new NavigationPage(new MainPage())
        {
            BarBackgroundColor = Color.FromArgb("#152238"),
            BarTextColor = Colors.White
        };
    }
}
