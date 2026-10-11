using System.Text.Json;

namespace VrcNetworkMonitor;

public static class StatsExport
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "VRChat", "VRChat", "stats.json");
    public static List<string[]> Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Expected VRChat export with a stats array");
        var rows = new List<string[]>();
        foreach (var stat in stats.EnumerateArray().Take(2000))
        {
            string Get(string key) => stat.TryGetProperty(key, out var value) ? value.ToString() : "—";
            rows.Add([Get("name"), Get("units"), Get("mean"), Get("tw-mean"), Get("min"), Get("max"), Get("total")]);
        }
        return rows;
    }
    public static void Show(Form parent)
    {
        using var form = new Form { Text = "VRChat network export — historical snapshot, not live counters", Width=1120, Height=680, StartPosition=FormStartPosition.CenterParent };
        var table = new DataGridView { Dock=DockStyle.Fill, ReadOnly=true, AllowUserToAddRows=false, RowHeadersVisible=false, AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill };
        foreach(var name in new[]{"Name","Units","Mean","Time-weighted mean","Minimum","Observed maximum","Total"})table.Columns.Add(name,name);
        table.Columns[0].FillWeight=220;
        var label = new Label { Dock=DockStyle.Top,Height=90,Padding=new Padding(10) };
        var controls = new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=42 };
        string path=DefaultPath;
        var filter = new TextBox { Width=230, PlaceholderText="Filter statistic name" };
        var loaded = new List<string[]>();
        void ApplyFilter() { table.Rows.Clear(); foreach(var row in loaded.Where(r=>r[0].Contains(filter.Text,StringComparison.OrdinalIgnoreCase)))table.Rows.Add(row.Cast<object>().ToArray()); }
        filter.TextChanged += (_,_)=>ApplyFilter();
        void Refresh()
        {
            try
            {
                var info = new FileInfo(path);
                if(!info.Exists) { label.Text="No stats.json export yet. In VRChat: Quick Menu → Settings → Toggle Debug UI → Players → export/dump statistics. Then click Refresh here.\n" + path; return; }
                if(info.Length>10*1024*1024)throw new InvalidDataException("Export exceeds 10 MiB viewer limit");
                using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var reader=new StreamReader(file);
                loaded=Parse(reader.ReadToEnd()); ApplyFilter();
                label.Text="Exported " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + " · snapshot age " + (DateTime.Now-info.LastWriteTime).TotalSeconds.ToString("N0") + " s\nHistorical snapshot: maxima are not capacity limits; raw unit labels can be wrong. Never sent as live chatbox stats.\n" + path;
            }
            catch(Exception ex) {label.Text="Could not read export: " + ex.Message;}
        }
        var refresh=new Button {Text="Refresh",Width=90};refresh.Click+=(_,_)=>Refresh();
        var open=new Button {Text="Open export…",Width=120};open.Click+=(_,_)=>{using var dialog=new OpenFileDialog{Filter="JSON files|*.json"};if(dialog.ShowDialog(form)==DialogResult.OK){path=dialog.FileName;Refresh();}};
        controls.Controls.AddRange([refresh,open,filter]);form.Controls.Add(table);form.Controls.Add(label);form.Controls.Add(controls);Refresh();form.ShowDialog(parent);
    }
}
