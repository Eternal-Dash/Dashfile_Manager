using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DashFile.Views;

public partial class PermissionDialog : Window
{
    private bool _loading;

    public PermissionDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
    }

    public string Result { get; private set; } = "644";

    public static async Task<string?> ShowAsync(Window owner, string path, string currentOctal)
    {
        var dlg = new PermissionDialog { Title = "权限" };
        dlg.FindControl<TextBlock>("PathText")!.Text = path;
        dlg.ApplyOctal(currentOctal);
        return await dlg.ShowDialog<string?>(owner);
    }

    private CheckBox C(string n) => this.FindControl<CheckBox>(n)!;

    private void ApplyOctal(string octal)
    {
        _loading = true;
        try
        {
            var s = (octal ?? "644").Trim();
            if (s.Length == 3) s = "0" + s;
            while (s.Length < 4) s = "0" + s;

            int su = s[0] - '0', u = s[1] - '0', g = s[2] - '0', o = s[3] - '0';
            SetTriplet(u, "U_R", "U_W", "U_X");
            SetTriplet(g, "G_R", "G_W", "G_X");
            SetTriplet(o, "O_R", "O_W", "O_X");
            C("SetUid").IsChecked = (su & 4) != 0;
            C("SetGid").IsChecked = (su & 2) != 0;
            C("Sticky").IsChecked = (su & 1) != 0;
            Refresh();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SetTriplet(int v, string r, string w, string x)
    {
        C(r).IsChecked = (v & 4) != 0;
        C(w).IsChecked = (v & 2) != 0;
        C(x).IsChecked = (v & 1) != 0;
    }

    private int Triplet(string r, string w, string x)
        => (C(r).IsChecked == true ? 4 : 0) + (C(w).IsChecked == true ? 2 : 0) + (C(x).IsChecked == true ? 1 : 0);

    private string BuildOctal()
    {
        int su = (C("SetUid").IsChecked == true ? 4 : 0) + (C("SetGid").IsChecked == true ? 2 : 0) + (C("Sticky").IsChecked == true ? 1 : 0);
        int u = Triplet("U_R", "U_W", "U_X");
        int g = Triplet("G_R", "G_W", "G_X");
        int o = Triplet("O_R", "O_W", "O_X");
        return su > 0 ? $"{su}{u}{g}{o}" : $"{u}{g}{o}";
    }

    private string BuildSymbolic()
    {
        string t(string r, string w, string x, bool su, bool sg, bool st)
        {
            var rd = C(r).IsChecked == true ? 'r' : '-';
            var wr = C(w).IsChecked == true ? 'w' : '-';
            var ex = C(x).IsChecked == true ? 'x' : '-';
            if (su) ex = C(x).IsChecked == true ? 's' : 'S';
            if (st) ex = C(x).IsChecked == true ? 't' : 'T';
            return $"{rd}{wr}{ex}";
        }
        return t("U_R","U_W","U_X", C("SetUid").IsChecked==true, false, false)
             + t("G_R","G_W","G_X", false, C("SetGid").IsChecked==true, false)
             + t("O_R","O_W","O_X", false, false, C("Sticky").IsChecked==true);
    }

    private void Refresh()
    {
        Result = BuildOctal();
        this.FindControl<TextBlock>("OctalText")!.Text = $"{BuildSymbolic()}({Result})";
    }

    private void OnAnyClick(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Refresh();
    }

    private void OnQuick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s) ApplyOctal(s);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Result);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
