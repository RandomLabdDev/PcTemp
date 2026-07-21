using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class UiPreview
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string directory = AppDomain.CurrentDomain.BaseDirectory;
        Assembly application = Assembly.LoadFrom(Path.Combine(directory, "PcTemp.exe"));
        Type dashboardType = application.GetType("PcTemp.DashboardForm", true);
        object[] arguments = { null, null, null, null, null, null, null, null };
        Form dashboard = (Form)dashboardType.GetConstructors()[0].Invoke(arguments);

        Type snapshotType = application.GetType("PcTemp.TemperatureSnapshot", true);
        object snapshot = Activator.CreateInstance(snapshotType);
        snapshotType.GetField("Cpu").SetValue(snapshot, 52F);
        snapshotType.GetField("Gpu").SetValue(snapshot, 43F);
        snapshotType.GetField("Board").SetValue(snapshot, 56F);
        snapshotType.GetField("CpuName").SetValue(snapshot, "Intel Core Ultra 7 2720K Plus");
        snapshotType.GetField("GpuName").SetValue(snapshot, "NVIDIA GeForce RTX 3070 Ti");
        snapshotType.GetField("BoardName").SetValue(snapshot, "Gigabyte B860 GAMING X WIFI6E");
        snapshotType.GetField("UpdatedAt").SetValue(snapshot, DateTime.Now);

        Type diskType = application.GetType("PcTemp.DiskTemperature", true);
        IList disks = (IList)snapshotType.GetField("Disks").GetValue(snapshot);
        string[] names =
        {
            "Samsung SSD 970", "Samsung SSD 850 PRO 256GB", "WDC WD20EARX-008FB0",
            "CT1000T710SSD8", "Kingston KC3000", "Seagate BarraCuda"
        };
        string[] types = { "M2", "SSD SATA", "SATA", "M2", "M2", "HDD" };
        for (int index = 0; index < names.Length; index++)
        {
            object disk = Activator.CreateInstance(diskType);
            diskType.GetField("Id").SetValue(disk, "preview-disk-" + index);
            diskType.GetField("Type").SetValue(disk, types[index]);
            diskType.GetField("Name").SetValue(disk, names[index]);
            diskType.GetField("Sensor").SetValue(disk, names[index]);
            diskType.GetField("Value").SetValue(disk, 31F + index);
            disks.Add(disk);
        }
        dashboardType.GetMethod("UpdateValues").Invoke(
            dashboard, new object[] { snapshot, 30, true, true, true });
        dashboard.Height = 900;
        Application.Run(dashboard);
    }
}
