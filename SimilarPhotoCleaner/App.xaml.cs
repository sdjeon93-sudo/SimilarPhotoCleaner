using System.Windows;

namespace SimilarPhotoCleaner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 폴더를 exe 위에 끌어다 놓거나 명령줄로 넘기면 바로 검사한다.
        var window = new MainWindow(e.Args.FirstOrDefault());
        window.Show();
    }
}
