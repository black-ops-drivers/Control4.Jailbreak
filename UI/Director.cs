namespace Garry.Control4.Jailbreak.UI
{
    public class Director
    {
        private readonly MainWindow _mainWindow;

        public Director(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
        }

        public void RefreshList()
        {
            using (var sddp = new Utility.Sddp())
            {
                sddp.OnResponse = r =>
                {
                    if (r.St != "c4:director")
                        return;

                    _ = Connect(r);
                };

                sddp.Search("c4:director");
            }
        }

        private bool Connect(Utility.Sddp.DeviceResponse connection)
        {
            try
            {
                _mainWindow.Jailbreak.IpAddress.Text = connection.EndPoint.Address.ToString();
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
