using System.Globalization;
using SonyCamera.Client;
namespace SonyCamera.Desktop;
/// <summary>Manual feature workbench. Reuses the managed client only; no SDK/NINA dependencies.</summary>
internal sealed class FeatureForm : Form {
    readonly SonyCameraClient client;
    readonly string receiveRoot;
    readonly ListBox properties=new(){Dock=DockStyle.Fill};
    readonly ComboBox choices=new(){Width=230,DropDownStyle=ComboBoxStyle.DropDownList};
    readonly NumericUpDown number=new(){Minimum=long.MinValue,Maximum=long.MaxValue,Width=160};
    readonly Label detail=new(){AutoSize=true,MaximumSize=new(650,0)};
    readonly PictureBox preview=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.Black};
    readonly TextBox log=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    readonly ListBox files=new(){Dock=DockStyle.Fill};
    readonly System.Windows.Forms.Timer timer=new(){Interval=150};
    readonly List<Button> actionButtons=new();
    readonly NumericUpDown speed=new(){Minimum=-127,Maximum=127,Value=1,Width=65};
    readonly NumericUpDown pulse=new(){Minimum=1,Maximum=2000,Value=100,Width=70};
    readonly TextBox exifTool=new(){Width=380};
    readonly CancellationTokenSource lifetime=new();
    CancellationTokenSource? downloadCancellation;
    bool busy,frameBusy,closing,canClose;
    public FeatureForm(SonyCameraClient client,string receiveRoot){
        this.client=client;this.receiveRoot=receiveRoot;
        Text="Sony A7 V · 扩展功能验证";Width=1200;Height=820;MinimumSize=new(900,650);Font=new("Microsoft YaHei UI",9);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new(8)};
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,70));root.RowStyles.Add(new(SizeType.Percent,30));
        root.Controls.Add(Row(new Label{Text="先停预览再拍 RAW；录像保存于相机卡。下载视频前停止录像并切换文件模式。",AutoSize=true}));
        var tabs=new TabControl{Dock=DockStyle.Fill};
        var settings=new TabPage("参数与状态");var split=new SplitContainer{Dock=DockStyle.Fill,Size=new(1100,450),SplitterDistance=350,FixedPanel=FixedPanel.Panel1};
        split.Panel1.Controls.Add(properties);var right=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,AutoScroll=true,WrapContents=false};
        right.Controls.Add(Button("读取功能清单",RefreshProperties));right.Controls.Add(detail);right.Controls.Add(Row(choices,number));
        right.Controls.Add(Button("写入选项并回读",async()=>{if(properties.SelectedItem is PropertyChoice p&&p.Probe.Info is {} info){long target=choices.SelectedItem is long x?x:(long)number.Value;await client.SetPropertyAsync(info.Property,target);await RefreshProperties();}}));
        right.Controls.Add(Row(new Label{Text="相对对焦速度 / 持续毫秒",AutoSize=true},speed,pulse,Button("移动并停止",()=>client.FocusPulseAsync((int)speed.Value,TimeSpan.FromMilliseconds((int)pulse.Value)))));
        right.Controls.Add(new Label{Text="光圈选项是 f 数 × 100；其余选项以相机回报的数值选择。只读项无法写入。\nFocusPosition 为镜头位置，不是毫米；望远镜无电子镜头时不提供。",AutoSize=true,MaximumSize=new(600,0)});
        split.Panel2.Controls.Add(right);settings.Controls.Add(split);tabs.TabPages.Add(settings);
        var live=new TabPage("实时预览与录像");var liveLayout=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};liveLayout.RowStyles.Add(new(SizeType.AutoSize));liveLayout.RowStyles.Add(new(SizeType.Percent,100));
        liveLayout.Controls.Add(Row(Button("开始预览",async()=>{await client.StartLiveViewAsync();timer.Start();}),Button("停止预览",StopPreview),Button("开始机内录像",()=>client.StartMovieAsync()),Button("停止机内录像",()=>client.StopMovieAsync())));liveLayout.Controls.Add(preview);live.Controls.Add(liveLayout);tabs.TabPages.Add(live);
        var card=new TabPage("相机卡原文件");var cardLayout=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};cardLayout.RowStyles.Add(new(SizeType.AutoSize));cardLayout.RowStyles.Add(new(SizeType.Percent,100));
        var cancel=new Button{Text="取消下载",AutoSize=true};cancel.Click+=(_,_)=>downloadCancellation?.Cancel();
        cardLayout.Controls.Add(Row(Button("切换为卡内文件模式",async()=>{await StopPreviewIfNeeded();if(client.MovieRecording)await client.StopMovieAsync();await SwitchMode(CameraConnectionMode.Contents);await RefreshFiles();}),Button("刷新卡内文件",RefreshFiles),Button("下载所选原文件",Download),cancel,Button("返回拍照模式",()=>SwitchMode(CameraConnectionMode.Remote))));cardLayout.Controls.Add(files);card.Controls.Add(cardLayout);tabs.TabPages.Add(card);
        var metadata=new TabPage("拍后温度");var metaLayout=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};
        metaLayout.Controls.Add(new Label{Text="温度从已拍 ARW 读取。选择本机合法安装的 ExifTool；缺失标签时显示未知。",AutoSize=true});
        metaLayout.Controls.Add(Row(exifTool,Button("选择 ExifTool",()=>{using var d=new OpenFileDialog{Filter="ExifTool|*.exe"};if(d.ShowDialog(this)==DialogResult.OK)exifTool.Text=d.FileName;return Task.CompletedTask;})));
        metaLayout.Controls.Add(Button("选择 ARW 并读取温度",async()=>{using var d=new OpenFileDialog{Filter="Sony RAW|*.arw"};if(d.ShowDialog(this)==DialogResult.OK){var t=await ArwMetadata.ReadTemperatureAsync(d.FileName,exifTool.Text,lifetime.Token);Write(t.Celsius is {} c?$"拍后温度 {c} °C；{t.SourceFile}":"此文件未读到温度标签，温度未知");}}));
        metadata.Controls.Add(metaLayout);tabs.TabPages.Add(metadata);
        root.Controls.Add(tabs);root.Controls.Add(log);Controls.Add(root);
        properties.SelectedIndexChanged+=(_,_)=>ShowProperty();choices.SelectedIndexChanged+=(_,_)=>{if(choices.SelectedItem is long x)number.Value=x;};
        timer.Tick+=async(_,_)=>{
            if(frameBusy||busy||closing||!client.LiveViewEnabled)return;frameBusy=true;
            try{var frame=await client.GetLiveViewFrameAsync(lifetime.Token);if(frame!=null){using var stream=new MemoryStream(frame.Jpeg);using var decoded=Image.FromStream(stream);var old=preview.Image;preview.Image=new Bitmap(decoded);old?.Dispose();}}
            catch(Exception ex){timer.Stop();Write("预览暂停："+ex.Message);}finally{frameBusy=false;}
        };
        FormClosing+=async(_,e)=>{if(canClose)return;e.Cancel=true;if(closing)return;closing=true;timer.Stop();downloadCancellation?.Cancel();lifetime.Cancel();
            try{while(busy||frameBusy)await Task.Delay(20);if(client.MovieRecording)await client.StopMovieAsync();if(client.LiveViewEnabled)await client.StopLiveViewAsync();}
            catch(Exception ex){Write("关闭清理："+ex.Message);MessageBox.Show(this,"相机状态需检查："+ex.Message,"关闭提示");}
            finally{canClose=true;Close();}};
        Shown+=async(_,_)=>{split.SplitterDistance=Math.Min(360,Math.Max(200,split.Width-500));await Run(async()=>{await client.CheckFeatureAbiAsync();await RefreshProperties();});};
    }
    static FlowLayoutPanel Row(params Control[] controls){var r=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};r.Controls.AddRange(controls);return r;}
    Button Button(string text,Func<Task> action){var b=new Button{Text=text,AutoSize=true};actionButtons.Add(b);b.Click+=async(_,_)=>await Run(action);return b;}
    async Task Run(Func<Task> action){if(busy||closing)return;busy=true;actionButtons.ForEach(b=>b.Enabled=false);try{await action();}catch(Exception ex){Write(ex.Message);}finally{busy=false;if(!closing)actionButtons.ForEach(b=>b.Enabled=true);}}
    void Write(string text){if(!IsDisposed)log.AppendText($"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");}
    async Task StopPreview(){timer.Stop();await client.StopLiveViewAsync();var old=preview.Image;preview.Image=null;old?.Dispose();}
    async Task StopPreviewIfNeeded(){if(client.LiveViewEnabled)await StopPreview();}
    async Task SwitchMode(CameraConnectionMode mode){await client.DisconnectAsync();var cameras=await client.EnumerateAsync();if(cameras.Count!=1)throw new InvalidOperationException("此工作台要求仅连接一台相机");await client.ConnectAsync(cameras[0].Index,mode);Write("连接模式："+mode);}
    async Task RefreshProperties(){var list=await client.GetPropertiesAsync();var selected=(properties.SelectedItem as PropertyChoice)?.Probe.Property;properties.Items.Clear();foreach(var p in list.Where(p=>p.Property is not (CameraProperty.FocusMagnifier or CameraProperty.FocusMagnificationTime or CameraProperty.InitialFocusMagnifier)))properties.Items.Add(new PropertyChoice(p));
        if(properties.Items.Count>0){var index=properties.Items.Cast<PropertyChoice>().ToList().FindIndex(p=>p.Probe.Property==selected);properties.SelectedIndex=Math.Max(0,index);}Write($"已查询 {list.Count} 项，{list.Count(p=>p.Available)} 项当前可读；不支持项保留原因。");}
    void ShowProperty(){choices.Items.Clear();if(properties.SelectedItem is not PropertyChoice p)return;if(p.Probe.Info is not {} info){detail.Text=p.Probe.Error?.Message??"不可用";return;}detail.Text=$"{info.Property}：当前 {info.Current}；{(info.Writable?"可写":"只读")}\n"+(info.IsRange?$"范围 {info.Minimum}..{info.Maximum}，步长 {info.Step}":$"候选 {string.Join(", ",info.Candidates)}");foreach(var x in info.Candidates)choices.Items.Add(x);if(choices.Items.Count>0)choices.SelectedIndex=0;number.Value=info.Current;}
    async Task RefreshFiles(){files.Items.Clear();foreach(var folder in await client.GetFoldersAsync())foreach(var file in await client.GetContentsAsync(folder))files.Items.Add(new ContentChoice(file));Write($"卡内原文件：{files.Items.Count}");}
    async Task Download(){if(files.SelectedItem is not ContentChoice selected)return;Directory.CreateDirectory(receiveRoot);using var cancellation=new CancellationTokenSource();downloadCancellation=cancellation;try{var t=await client.DownloadContentAsync(selected.File,receiveRoot,TimeSpan.FromMinutes(30),new Progress<ContentTransfer>(p=>Text=$"原文件下载 · {p.VerifiedBytes}/{p.ExpectedBytes} 字节已验证"),cancellation.Token);Write(t.IsSuccess?$"原文件下载成功：{t.Path}（{t.VerifiedBytes} 字节）":$"下载失败：{t.Failure.Message}；请断开后重新连接");}finally{downloadCancellation=null;}}
    protected override void Dispose(bool disposing){if(disposing){timer.Dispose();lifetime.Dispose();preview.Image?.Dispose();}base.Dispose(disposing);}
    sealed record PropertyChoice(PropertyProbe Probe){public override string ToString()=>$"{Probe.Property} · {(Probe.Available?(Probe.Info!.Writable?"可写":"只读"):"不可用")}";}
    sealed record ContentChoice(CameraContent File){public override string ToString()=>$"{File.Name} · {File.Bytes} 字节 · {File.CameraDate}";}
}
