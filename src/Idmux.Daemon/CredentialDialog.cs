using System.Security;
using System.Windows;
using System.Windows.Controls;

namespace Idmux.Daemon;

internal static class CredentialDialog
{
    public static (bool Ok, string UserName, SecureString? Password) Prompt(string suggestedUser, string caption)
    {
        (bool Ok, string UserName, SecureString? Password) result = (false, suggestedUser, null);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window
                {
                    Title = caption,
                    Width = 420,
                    Height = 230,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ResizeMode = ResizeMode.NoResize,
                    Topmost = true,
                    ShowInTaskbar = true
                };

                var grid = new Grid { Margin = new Thickness(16) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());

                var intro = new TextBlock
                {
                    Text = "Enter the credential to hold in the daemon for this Windows session.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                Grid.SetRow(intro, 0);
                Grid.SetColumnSpan(intro, 2);
                grid.Children.Add(intro);

                var userLabel = new TextBlock { Text = "Username:", VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(userLabel, 1);
                Grid.SetColumn(userLabel, 0);
                grid.Children.Add(userLabel);

                var userBox = new TextBox { Text = suggestedUser, Margin = new Thickness(8, 0, 0, 8) };
                Grid.SetRow(userBox, 1);
                Grid.SetColumn(userBox, 1);
                grid.Children.Add(userBox);

                var passwordLabel = new TextBlock { Text = "Password:", VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(passwordLabel, 2);
                Grid.SetColumn(passwordLabel, 0);
                grid.Children.Add(passwordLabel);

                var passwordBox = new PasswordBox { Margin = new Thickness(8, 0, 0, 12) };
                Grid.SetRow(passwordBox, 2);
                Grid.SetColumn(passwordBox, 1);
                grid.Children.Add(passwordBox);

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetRow(actions, 4);
                Grid.SetColumnSpan(actions, 2);

                var cancel = new Button
                {
                    Content = "Cancel",
                    Width = 90,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsCancel = true
                };
                cancel.Click += (_, _) =>
                {
                    result = (false, userBox.Text, null);
                    window.DialogResult = false;
                    window.Close();
                };

                var ok = new Button
                {
                    Content = "OK",
                    Width = 90,
                    IsDefault = true
                };
                ok.Click += (_, _) =>
                {
                    result = (true, userBox.Text, passwordBox.SecurePassword.Copy());
                    window.DialogResult = true;
                    window.Close();
                };

                actions.Children.Add(cancel);
                actions.Children.Add(ok);
                grid.Children.Add(actions);

                window.Content = grid;
                window.Loaded += (_, _) =>
                {
                    window.Activate();
                    userBox.Focus();
                    userBox.SelectAll();
                };

                _ = window.ShowDialog();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}
