using System.Windows;
using System.Windows.Input;
using TrainerStudio.Core;

namespace TrainerStudio.App;

public partial class TestGameWindow : Window
{
    private readonly OwnedTestTarget _target;
    private readonly Action<bool, string> _onAction;
    private readonly Action _onStop;

    public TestGameWindow(OwnedTestTarget target, Action<bool, string> onAction, Action onStop)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _onAction = onAction ?? throw new ArgumentNullException(nameof(onAction));
        _onStop = onStop ?? throw new ArgumentNullException(nameof(onStop));
        InitializeComponent();
        RefreshState();
    }

    public void RefreshState()
    {
        StateText.Text = $"Valuta:  {_target.Currency}\nLiv:     {_target.Health}\nAmmo:    {_target.Ammo}\nStamina: {_target.Stamina}\nTimer:   {_target.Timer}";
    }

    private void Collect_Click(object sender, RoutedEventArgs e) => Apply(_target.CollectCurrency, "Valuta steg med 25.");

    private void Spend_Click(object sender, RoutedEventArgs e) => Apply(_target.SpendCurrency, "Valuta faldt med 10.");

    private void Damage_Click(object sender, RoutedEventArgs e) => Apply(_target.TakeDamage, "Liv faldt med 15.");

    private void Heal_Click(object sender, RoutedEventArgs e) => Apply(_target.Heal, "Liv steg med 10.");

    private void Fire_Click(object sender, RoutedEventArgs e) => Apply(_target.Fire, "Ammunition faldt med 1.");

    private void Reload_Click(object sender, RoutedEventArgs e) => Apply(_target.Reload, "Ammunition steg med 5.");

    private void DrainStamina_Click(object sender, RoutedEventArgs e) => Apply(_target.DrainStamina, "Stamina faldt med 20.");

    private void TickTimer_Click(object sender, RoutedEventArgs e) => Apply(_target.TickTimer, "Timeren faldt med 1.");

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _target.Reset();
        RefreshState();
        StatusText.Text = "Testspillet blev nulstillet. Ryd søgningen i hovedvinduet, før du søger igen.";
        _onAction(true, "Testspillet blev nulstillet fra testvinduet.");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _onStop();
            StatusText.Text = "Alle aktive fastholdelser er stoppet.";
            e.Handled = true;
        }
    }

    private void Apply(Action action, string message)
    {
        action();
        RefreshState();
        StatusText.Text = message;
        _onAction(false, message);
    }
}
