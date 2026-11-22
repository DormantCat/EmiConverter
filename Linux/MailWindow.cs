using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace EmiConverter;

internal sealed class MailWindow : Window
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly DomainLookup whois;
    private readonly DossierLookup dossier;
    private readonly TextBlock status = Label("Файл не открыт", 12);
    private readonly ListBox links = new();
    private readonly TextBox address = Box(), domain = Box(false), target = Box(false), network = Box(), registration = Box();
    private readonly ContentControl letter = new();
    private readonly TabControl tools = new();
    private readonly List<Button> selectionActions = [];
    private readonly TextBlock count = Label("Ссылки", 14, true);
    private Control emptyLinks = null!;
    private bool dark = true;
    private int generation;
    private readonly CancellationTokenSource closing = new();
    private static readonly string ThemeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EmiConverter","theme.txt");

    public MailWindow(string? initialPath)
    {
        whois = new(http); dossier = new(http);
        Title = "EMI Converter"; Width = 1180; Height = 760; MinWidth = 920; MinHeight = 620;
        SystemDecorations = SystemDecorations.None;
        try { dark = !File.Exists(ThemeFile) || File.ReadAllText(ThemeFile).Trim() != "light"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var root = GridOf("*,Auto","56,*,28"); root.Margin = new Thickness(20,9,20,6); Content = root;
        var top = GridOf("Auto,*,Auto","*"); Place(root,top,0,0,2);
        var brand = new StackPanel { Orientation=Orientation.Horizontal, Spacing=18, VerticalAlignment=VerticalAlignment.Center };
        var badge = new Border { Width=40,Height=40,CornerRadius=new CornerRadius(10),Background=Yellow,Child=Centered("✉",25,Brushes.Black) };
        brand.Children.Add(badge); brand.Children.Add(Label("EMI Converter",27,true)); Place(top,brand,0,0);
        var drag = new Border { Background=Brushes.Transparent }; Place(top,drag,1,0);
        drag.PointerPressed += (_,e) => { if(e.ClickCount==2) Maximize(); else BeginMoveDrag(e); };
        var controls = Row();
        var theme = Button("",() => {}); theme.Content=dark?"☼   Светлая":"☾   Тёмная";
        theme.Click += (_,_) => {dark=!dark; ApplyTheme();theme.Content=dark?"☼   Светлая":"☾   Тёмная";
            try {Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);File.WriteAllText(ThemeFile,dark?"dark":"light");} catch(Exception ex) when (ex is IOException or UnauthorizedAccessException){status.Text="Не удалось сохранить тему";}};
        controls.Children.Add(theme); controls.Children.Add(Button("Вставить исходник",async () => await PasteDialog()));
        controls.Children.Add(Button("Открыть файл…",async () => await Choose(),true));
        controls.Children.Add(Button("−",() => WindowState=WindowState.Minimized)); controls.Children.Add(Button("□",Maximize));controls.Children.Add(Button("×",Close));
        Place(top,controls,2,0);
        var columns = GridOf("62*,16,36*","*");columns.Margin=new Thickness(0,9,0,6);Place(root,columns,0,1,2);
        Place(columns,Card(letter,18),0,0);Place(columns,Card(tools,18),2,0);
        letter.Content=Empty("Откройте письмо","Перетащите файл сюда или выберите его на диске",true);
        tools.ItemsSource=new[]{Tab(count,LinksPane()),Tab("WHOIS",LookupPane()),Tab("Данные",DataPane())};
        Place(root,status,0,2);Place(root,Label($"v{typeof(MailWindow).Assembly.GetName().Version?.ToString(3)}    ·    EML / EMI / TXT",12),1,2);
        DragDrop.SetAllowDrop(this,true);
        AddHandler(DragDrop.DragOverEvent, (_,e) => e.DragEffects=e.Data.Contains(DataFormats.Files)?DragDropEffects.Copy:DragDropEffects.None);
        AddHandler(DragDrop.DropEvent,async (_,e) => {if(e.Data.GetFiles()?.FirstOrDefault() is IStorageFile file)await Open(file);});
        Closed += (_,_) => {closing.Cancel();http.Dispose();};
        ApplyTheme();
        if(!string.IsNullOrEmpty(initialPath)) Opened += async (_,_) => {
            try {await using var s=File.OpenRead(initialPath);await Read(s,Path.GetFileName(initialPath));}catch(Exception ex){status.Text=ex.Message;}};
    }
    static readonly IBrush Yellow=Brush.Parse("#FFC934");
    void Maximize()=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    void ApplyTheme()
    {
        RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light;
        Background=Brush.Parse(dark?"#1E1D1C":"#F5F5F7");
        Resources["CardColor"]=Brush.Parse(dark?"#282624":"#FFFFFF");
        Resources["FieldColor"]=Brush.Parse(dark?"#34312E":"#F0F0F3");
        Resources["SystemAccentColor"]=Color.Parse("#FFC934");
    }
    static TextBlock Label(string s,double size,bool bold=false)=>new(){Text=s,FontSize=size,FontWeight=bold?FontWeight.Bold:FontWeight.Normal,VerticalAlignment=VerticalAlignment.Center};
    static TextBlock Centered(string s,double size,IBrush brush)=>new(){Text=s,FontSize=size,Foreground=brush,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    static Grid GridOf(string columns,string rows)=>new(){ColumnDefinitions=new ColumnDefinitions(columns),RowDefinitions=new RowDefinitions(rows)};
    static void Place(Grid g,Control c,int col,int row,int span=1){Grid.SetColumn(c,col);Grid.SetRow(c,row);Grid.SetColumnSpan(c,span);g.Children.Add(c);}
    static StackPanel Row()=>new(){Orientation=Orientation.Horizontal,Spacing=7,VerticalAlignment=VerticalAlignment.Center};
    static TextBox Box(bool readOnly=true)=>new(){IsReadOnly=readOnly,AcceptsReturn=readOnly,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0)};
    static TabItem Tab(object title,Control content)=>new(){Header=title,Content=content};
    Border Card(Control c,int pad)
    {
        var card=new Border{CornerRadius=new CornerRadius(12),Padding=new Thickness(pad),Child=c};
        card.Bind(Border.BackgroundProperty,this.GetResourceObservable("CardColor"));return card;
    }
    Button Button(string caption,Action action,bool primary=false)
    {
        var btn=new Button{Content=caption,CornerRadius=new CornerRadius(6),Padding=new Thickness(12,7),BorderThickness=new Thickness(0),FontWeight=FontWeight.SemiBold};
        if(primary){btn.Background=Yellow;btn.Foreground=Brushes.Black;}
        else btn.Bind(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty,this.GetResourceObservable("FieldColor"));
        btn.Click+=(_,_)=>action();return btn;
    }
    Control Empty(string title,string subtitle,bool file=false)
    {
        var panel=new StackPanel{Spacing=12,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
        if(file)panel.Children.Add(new Border{Width=64,Height=64,CornerRadius=new CornerRadius(16),HorizontalAlignment=HorizontalAlignment.Center,Background=Brush.Parse("#584B27"),Child=Centered("✉",37,Yellow)});
        var h=Label(title,file?28:17,true);h.HorizontalAlignment=HorizontalAlignment.Center;panel.Children.Add(h);panel.Children.Add(Label(subtitle,13));
        if(file){var button=Button("Выбрать файл",async()=>await Choose(),true);button.HorizontalAlignment=HorizontalAlignment.Center;panel.Children.Add(button);}
        return panel;
    }
    async Task Choose()
    {
        try {var files=await StorageProvider.OpenFilePickerAsync(new(){Title="Открыть письмо",AllowMultiple=false});if(files.Count>0)await Open(files[0]);}
        catch(Exception ex){status.Text=ex.Message;}
    }
    async Task Open(IStorageFile file)
    {
        try{await using var stream=await file.OpenReadAsync();await Read(stream,file.Name);}catch(Exception ex){status.Text=ex.Message;}
    }
    async Task Read(Stream stream,string name)
    {
        int request=++generation;
        using var bytes=new MemoryStream();byte[] buffer=new byte[65536];int n;
        while((n=await stream.ReadAsync(buffer,closing.Token))>0){
            if(bytes.Length+n>MailReader.MaxFileBytes)throw new IOException("Максимальный размер письма — 32 МиБ.");
            bytes.Write(buffer,0,n);
        }
        if(bytes.Length==0)throw new IOException("Письмо пустое.");
        var mail=await Task.Run(()=>MailReader.Parse(bytes.ToArray()),closing.Token);if(request!=generation)return;
        var page=GridOf("*","Auto,Auto,*");Place(page,Label(mail.Subject.Length==0?"Без темы":mail.Subject,23,true),0,0);
        var metadata=Box();metadata.Text=string.Join("\n",new[]{("От",mail.From),("Кому",mail.To),("Копия",mail.Cc),("Скрытая копия",mail.Bcc),("Ответить",mail.ReplyTo),("Дата",mail.Date)}.Where(x=>x.Item2.Length>0).Select(x=>x.Item1+": "+x.Item2));Place(page,metadata,0,1);
        var body=Box();body.Text=mail.Body;var source=Box();source.Text=mail.Source;
        Place(page,new TabControl{ItemsSource=new[]{Tab("Текст",body),Tab("Исходник",source)}},0,2);
        letter.Content=page;links.ItemsSource=mail.Links;links.SelectedIndex=mail.Links.Count>0?0:-1;emptyLinks.IsVisible=mail.Links.Count==0;links.IsVisible=!emptyLinks.IsVisible;
        count.Text="Ссылки · "+mail.Links.Count;status.Text=name;
    }
    async Task PasteDialog()
    {
        var input=Box(false);input.AcceptsReturn=true;input.Watermark="Вставьте исходный текст письма";
        var dlg=new Window{Title="Вставить исходник",Width=650,Height=420,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var pane=GridOf("*","*,Auto");pane.Margin=new Thickness(14);Place(pane,input,0,0);Place(pane,Button("Открыть",()=>dlg.Close(true),true),0,1);dlg.Content=pane;
        if(await dlg.ShowDialog<bool>(this)){
            try{using var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input.Text??""));await Read(stream,"Вставленный исходник");}catch(Exception ex){status.Text=ex.Message;}}
    }
    Control LinksPane()
    {
        var pane=GridOf("*","*,72,Auto,Auto");var overlay=new Grid();Place(pane,overlay,0,0);links.IsVisible=false;overlay.Children.Add(links);
        emptyLinks=Empty("Ссылок пока нет","Здесь появятся ссылки из письма");overlay.Children.Add(emptyLinks);address.Watermark="Полный адрес выбранной ссылки";Place(pane,address,0,1);
        links.SelectionChanged+=(_,_)=>{address.Text=links.SelectedItem as string;foreach(var b in selectionActions)b.IsEnabled=address.Text?.Length>0;};
        var row=Row();var more=Row();Place(pane,row,0,2);Place(pane,more,0,3);
        selectionActions.Add(Button("Проверить домен",async()=>{tools.SelectedIndex=1;domain.Text=address.Text;await Lookup();}));
        selectionActions.Add(Button("Копировать",async()=>await Copy(address.Text)));
        selectionActions.Add(Button("Куда ведёт",async()=>{var url=address.Text;if(url==null)return;tools.SelectedIndex=2;await Run(network,()=>RedirectInspector.InspectAsync(url,closing.Token));}));
        foreach(var b in selectionActions)b.IsEnabled=false;
        row.Children.Add(selectionActions[0]);row.Children.Add(selectionActions[1]);more.Children.Add(selectionActions[2]);
        more.Children.Add(Button("Копировать все",async()=>await Copy(string.Join("\n",links.Items.OfType<string>()))));return pane;
    }
    async Task Copy(string? s){try{if(!string.IsNullOrEmpty(s)&&Clipboard!=null)await Clipboard.SetTextAsync(s);}catch(Exception ex){status.Text=ex.Message;}}
    async Task Run(TextBox view,Func<Task<string>> work){try{view.Text="Выполняется проверка…";view.Text=await work();}catch(Exception ex){view.Text=ex.Message;}}
    Task Lookup()=>Run(registration,async()=>{var name=DomainLookup.FromInput(domain.Text??"");return name==null?"Укажите домен":(await whois.QueryAsync(name,closing.Token)).Report;});
    Control LookupPane()
    {
        var g=GridOf("*","Auto,Auto,*,Auto");domain.Watermark="Домен, email или ссылка";Place(g,domain,0,0);Place(g,Button("Проверить",async()=>await Lookup(),true),0,1);Place(g,registration,0,2);Place(g,Button("Копировать",async()=>await Copy(registration.Text)),0,3);return g;
    }
    Control DataPane()
    {
        var g=GridOf("*","Auto,Auto,*,Auto");target.Watermark="Домен, URL, email или IP";Place(g,target,0,0);
        Place(g,Button("Проверить всё",async()=>await Run(network,async()=>{
            var name=DossierLookup.ParseTarget(target.Text??"");if(name==null)return "Укажите домен или IP";
            var parts=await Task.WhenAll(Enum.GetValues<DossierSection>().Select(async section=>{
                try{return section+"\n"+(section==DossierSection.DomainWhois?(System.Net.IPAddress.TryParse(name,out _)?"Для IP см. NetworkWhois":(await whois.QueryAsync(name,closing.Token)).Report):await dossier.QueryAsync(section,name,closing.Token));}
                catch(Exception ex){return section+"\n"+ex.Message;}}));return string.Join("\n\n",parts);}),true),0,1);
        Place(g,network,0,2);Place(g,Button("Копировать",async()=>await Copy(network.Text)),0,3);return g;
    }
}
