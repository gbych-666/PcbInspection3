using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;


namespace PcbInspection
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string selectedRole = ((ComboBoxItem)CmbRole.SelectedItem).Content.ToString();
            string password = TxtPassword.Password;

            // 简易密码校验（后续可对接数据库或加密配置文件）
            bool isValid = false;
            if (selectedRole == "操作员" && password == "123") isValid = true;
            if (selectedRole == "管理员" && password == "666") isValid = true;

            if (isValid)
            {
                MainWindow mainWindow = new MainWindow(selectedRole);
                mainWindow.Show();
                this.Close();
            }
            else
            {
                TxtError.Text = "密码错误，请重新输入！";
                TxtError.Visibility = Visibility.Visible;
            }

        }
    }
}
