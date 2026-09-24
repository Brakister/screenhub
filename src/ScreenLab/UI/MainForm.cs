using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp;
using ScreenLab.Capture;
using ScreenLab.Services;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace ScreenLab.UI;

/// <summary>
/// Janela principal: pré-visualização ao vivo, seleção de usuário,
/// modos de captura e bandeja do sistema para operação 24/7.
/// </summary>
public class MainForm : Form
{
    private const int SideWidth = 252;            // painel lateral (operadores + captura)
    private const int SettingsGroupWidth = 560;   // largura dos grupos na página de configurações
    private const int HeaderHeight = 36;          // barra superior com o LED

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_CAPTURE = 1;
    private const int HOTKEY_ID_PAUSE = 2;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly AppConfig _config;
    private readonly UserManager _users;
    private readonly CaptureEngine _engine;

    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayPause = new("Pausar") { CheckOnClick = false };

    private bool _exiting;
    private bool _isHiddenToTray;
    private bool _loadingUi;
    private bool _hotkeysRegistered;
    private bool _countdownActive;
    private bool _settingsOpen;

    // Pré-visualização
    private PictureBox _preview = null!;
    private ToolStripStatusLabel _lblStatus = null!;
    private ToolStripStatusLabel _lblLast = null!;
    private StatusStrip _statusStrip = null!;

    // Barra de status superior (LED + indicadores)
    private LedControl _led = null!;
    private Label _lblHeaderStatus = null!;
    private Label _lblHeaderLast = null!;

    // Painel lateral (operadores + captura)
    private Label _lblOperatorsTitle = null!;
    private Panel _operatorsScroll = null!;
    private RoundedButton _btnCapture = null!;
    private RoundedButton _btnPause = null!;
    private RoundedButton _btnSettings = null!;

    // Página de configurações (fica escondida até clicar em "Configurações")
    private Panel _settingsPage = null!;
    private RoundedButton _btnBack = null!;
    private Label _lblCountdown = null!;

    // Controles usados na página de configurações
    private ComboBox _cboUser = null!;
    private TextBox _txtNewUser = null!;
    private Button _btnRemoveUser = null!;
    private NumericUpDown _numCooldown = null!;
    private ComboBox _cboCamera = null!;
    private Button _btnTestCamera = null!;
    private TextBox _txtOutput = null!;
    private CheckBox _ckNotifications = null!;
    private CheckBox _ckStartup = null!;

    public MainForm(bool startMinimized)
    {
        _config = ConfigService.Load();
        _users = new UserManager(_config);
        _engine = new CaptureEngine(_config, _users);

        BuildUi();
        BuildTray();
        LoadConfigIntoUi();

        // Mantém a lista de operadores e os botões grandes sincronizados.
        _users.UsersChanged += () => SafeBeginInvoke(() =>
        {
            ReloadUserCombo();
            RebuildOperatorButtons();
        });
        _users.ActiveUserChanged += () => SafeBeginInvoke(RebuildOperatorButtons);

        WireEngineEvents();
        RegisterHotkeys();

        _engine.Start();

        if (startMinimized)
        {
            // Garante o modo econômico (leitura lenta) mesmo antes do form ser exibido.
            _engine.SetPreviewWanted(false);
            Shown += (s, e) => SafeBeginInvoke(HideToTray);
        }
        else
        {
            _engine.SetPreviewWanted(true);
        }
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        Text = "ScreenLab — Captura 24/7";
        Icon = AppIcons.Icon;
        Width = 1120;
        Height = 680;
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        _statusStrip = new StatusStrip { SizingGrip = false };
        _lblStatus = new ToolStripStatusLabel("Iniciando...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _lblLast = new ToolStripStatusLabel("Última foto: —") { TextAlign = ContentAlignment.MiddleRight };
        _statusStrip.Items.Add(_lblStatus);
        _statusStrip.Items.Add(_lblLast);
        Controls.Add(_statusStrip);

        // Área central: cabeçalho (LED) + painel lateral + pré-visualização.
        // A barra de status fica DEBAIXO de tudo (largura total), sem "degrau".
        var mid = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        mid.Controls.Add(BuildHeaderStrip());
        mid.Controls.Add(BuildSidePanel());

        _preview = new BufferedPictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
        };
        mid.Controls.Add(_preview);
        _preview.BringToFront();

        // Contagem regressiva de 1 s (sobreposto à pré-visualização)
        _lblCountdown = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(160, 0, 0, 0),
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 60f, FontStyle.Bold),
            Visible = false,
        };
        _preview.Controls.Add(_lblCountdown);
        _preview.Resize += (s, e) => CenterCountdown();
        CenterCountdown();

        Controls.Add(mid);

        // Página de configurações: fica escondida até clicar em "Configurações"
        _settingsPage = BuildSettingsPage();
        _settingsPage.Visible = false;
        Controls.Add(_settingsPage);
        Resize += (s, e) => PositionSettingsPage();
        PositionSettingsPage();
    }

    /// <summary>Painel lateral: operadores em botões grandes + ações principais.</summary>
    private Panel BuildSidePanel()
    {
        var panel = new Panel { Dock = DockStyle.Right, Width = SideWidth, BackColor = Color.FromArgb(36, 36, 40) };

        _lblOperatorsTitle = new Label
        {
            Dock = DockStyle.Fill,
            Text = "  OPERADORES",
            ForeColor = Color.FromArgb(190, 190, 195),
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };

        var bottom = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(28, 28, 32),
        };

        _btnCapture = new RoundedButton
        {
            Text = "TIRAR FOTO (ESPAÇO)",
            ButtonColor = Color.FromArgb(0, 140, 60),
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            Location = new Point(10, 12),
            Size = new Size(SideWidth - 20, 58),
        };
        _btnCapture.Click += (s, e) => RequestPhoto();

        _btnSettings = new RoundedButton
        {
            Text = "Configurações",
            ButtonColor = Color.FromArgb(72, 72, 80),
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            Location = new Point(10, 80),
            Size = new Size(112, 40),
        };
        _btnSettings.Click += (s, e) => ShowSettingsPage();

        _btnPause = new RoundedButton
        {
            Text = "Pausar (F8)",
            ButtonColor = Color.FromArgb(110, 70, 25),
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            Location = new Point(130, 80),
            Size = new Size(112, 40),
        };
        _btnPause.Click += (s, e) => TogglePause();

        var hint = new Label
        {
            Text = "A tecla ESPAÇO também tira foto",
            ForeColor = Color.FromArgb(120, 120, 128),
            Font = new Font(Font.FontFamily, 8f),
            Location = new Point(10, 130),
            AutoSize = true,
        };

        bottom.Controls.Add(_btnCapture);
        bottom.Controls.Add(_btnSettings);
        bottom.Controls.Add(_btnPause);
        bottom.Controls.Add(hint);

        _operatorsScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(36, 36, 40),
        };
        _operatorsScroll.Resize += (s, e) => RebuildOperatorButtons();

        // Grade determinística (título / operadores / ações) — evita o conflito
        // de Dock que escondia os botões atrás da lista de operadores.
        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // título
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // operadores
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));  // ações

        tlp.Controls.Add(_lblOperatorsTitle, 0, 0);
        tlp.Controls.Add(_operatorsScroll, 0, 1);
        tlp.Controls.Add(bottom, 0, 2);

        panel.Controls.Add(tlp);
        return panel;
    }

    /// <summary>Recria os botões grandes dos operadores, marcando o selecionado.</summary>
    private void RebuildOperatorButtons()
    {
        if (_operatorsScroll == null || !_operatorsScroll.Visible)
            return;

        string active = _users.ActiveUser;
        int w = Math.Max(140, _operatorsScroll.ClientSize.Width - 20);
        int y = 8;

        _operatorsScroll.SuspendLayout();
        _operatorsScroll.Controls.Clear();
        foreach (string name in _users.Users)
        {
            bool selected = name == active;
            var btn = new RoundedButton
            {
                Text = selected ? "✓  " + name : name,
                ButtonColor = selected ? Color.FromArgb(0, 140, 70) : Color.FromArgb(52, 92, 130),
                ForeColor = Color.White,
                Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
                Location = new Point(10, y),
                Size = new Size(w, 52),
            };
            string op = name;
            btn.Click += (s, e) => SelectOperator(op);
            _operatorsScroll.Controls.Add(btn);
            y += 62;
        }
        _operatorsScroll.AutoScrollMinSize = new Size(0, y + 6);
        _operatorsScroll.ResumeLayout();
    }

    private void SelectOperator(string name)
    {
        if (_users.ActiveUser != name)
            _users.ActiveUser = name; // persiste o operador selecionado
        else
            RebuildOperatorButtons();
    }

    private void ShowSettingsPage()
    {
        _settingsOpen = true;
        _settingsPage.Visible = true;
        _settingsPage.BringToFront();
    }

    private void CloseSettingsPage()
    {
        _settingsOpen = false;
        _settingsPage.Visible = false;
    }

    private void PositionSettingsPage()
    {
        if (_settingsPage == null || !IsHandleCreated)
            return;
        int top = HeaderHeight;
        int bottomStrip = _statusStrip?.Height ?? 22;
        _settingsPage.SetBounds(0, top, ClientSize.Width, Math.Max(0, ClientSize.Height - top - bottomStrip));
    }

    /// <summary>Página com todas as configurações (fechada = "escondida").</summary>
    private Panel BuildSettingsPage()
    {
        var page = new Panel { BackColor = Color.FromArgb(30, 30, 36) };

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 22, 28) };
        var title = new Label
        {
            Text = "Configurações",
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Padding = new Padding(16, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _btnBack = new RoundedButton
        {
            Text = "←  Voltar",
            ButtonColor = Color.FromArgb(70, 70, 80),
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Width = 130,
        };
        _btnBack.Click += (s, e) => CloseSettingsPage();

        var htlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        htlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        htlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        htlp.Controls.Add(title, 0, 0);
        htlp.Controls.Add(_btnBack, 1, 0);
        header.Controls.Add(htlp);

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(30, 30, 36),
        };

        // Coluna central com largura confortável: os grupos param de esticar até a
        // borda (a página ficava com caixas gigantes e os controles espremidos à esquerda).
        var col = new Panel { Width = SettingsGroupWidth, BackColor = Color.FromArgb(30, 30, 36) };
        AddGroup(col, BuildCaptureSettingsGroup());
        AddGroup(col, BuildUserGroup());
        AddGroup(col, BuildCameraGroup());
        AddGroup(col, BuildGeneralGroup());
        int colH = 14;
        foreach (Control c in col.Controls) colH += c.Height + 8;
        col.Height = colH;
        scroll.Resize += (s, e) =>
            col.Location = new Point(Math.Max(0, (scroll.ClientSize.Width - col.Width) / 2), 0);
        scroll.Controls.Add(col);
        scroll.AutoScrollMinSize = new Size(0, colH + 20);

        // Grade: cabeçalho fixo em cima, rolagem do conteúdo no resto.
        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        tlp.Controls.Add(header, 0, 0);
        tlp.Controls.Add(scroll, 0, 1);

        page.Controls.Add(tlp);
        return page;
    }

    private GroupBox BuildCaptureSettingsGroup()
    {
        var gb = NewGroup("Captura");
        int y = 10;

        _numCooldown = AddNumRow(gb, "Largura mín. entre fotos (s):", 12, y, 0, 3600, 1);
        _numCooldown.ValueChanged += (s, e) => { _config.CooldownSeconds = (int)_numCooldown.Value; SaveConfig(); };
        y += 30;

        var hint = new Label
        {
            Text = "• ESPAÇO ou F9 tiram a foto com 1 s de previsão\n• A luz fica verde no momento da foto",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Location = new Point(12, y),
        };
        gb.Controls.Add(hint);
        y += hint.Height + 10;

        gb.Height = y + 6;
        return gb;
    }

    // Order adicionada = ordem vertical (Dock = Top, primeira adicionada fica no topo).
    private void AddGroup(Panel host, GroupBox box)
    {
        box.Dock = DockStyle.Top;
        host.Controls.Add(box);
        host.Controls.SetChildIndex(box, 0);
    }

    /// <summary>Barra acima da pré-visualização com a luz de status (LED).</summary>
    private Panel BuildHeaderStrip()
    {
        var strip = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Color.FromArgb(30, 30, 30),
        };

        _led = new LedControl { Anchor = AnchorStyles.Left | AnchorStyles.Top };
        _lblHeaderStatus = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(235, 235, 235),
            Text = "Iniciando...",
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
        };
        _lblHeaderLast = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(175, 175, 175),
            Text = "Última foto: —",
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(10, 4, 10, 4),
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        tlp.Controls.Add(_led, 0, 0);
        tlp.Controls.Add(_lblHeaderStatus, 1, 0);
        tlp.Controls.Add(_lblHeaderLast, 2, 0);

        strip.Controls.Add(tlp);
        return strip;
    }

    private GroupBox BuildUserGroup()
    {
        var gb = NewGroup("Usuário");
        int y = 12;

        AddLabel(gb, "Selecione quem está operando:", 12, y);
        y += 22;

        _cboUser = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 312,
            Location = new Point(12, y),
        };
        _cboUser.SelectedIndexChanged += (s, e) =>
        {
            if (_loadingUi || _cboUser.SelectedItem is not string user) return;
            _users.ActiveUser = user;
        };
        gb.Controls.Add(_cboUser);
        y += 30;

        _txtNewUser = new TextBox
        {
            Width = 228,
            Location = new Point(12, y),
        };
        _txtNewUser.TextChanged += (s, e) =>
            _txtNewUser.ForeColor = string.IsNullOrEmpty(_txtNewUser.Text) ? Color.Gray : Color.Black;
        _txtNewUser.GotFocus += (s, e) =>
        {
            if (_txtNewUser.Text == "Novo usuário..." && _txtNewUser.ForeColor == Color.Gray)
            {
                _txtNewUser.Text = "";
                _txtNewUser.ForeColor = Color.Black;
            }
        };
        var btnAdd = new Button
        {
            Text = "Adicionar",
            Width = 82,
            Height = 25,
            FlatStyle = FlatStyle.System,
            Location = new Point(246, y - 1),
        };
        btnAdd.Click += (s, e) => AddNewUser();
        _txtNewUser.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddNewUser(); e.SuppressKeyPress = true; } };

        _btnRemoveUser = new Button
        {
            Text = "Remover selecionado",
            Width = 312,
            Height = 25,
            FlatStyle = FlatStyle.System,
            Location = new Point(12, y + 31),
        };
        _btnRemoveUser.Click += (s, e) => RemoveSelectedUser();

        gb.Controls.Add(_txtNewUser);
        gb.Controls.Add(btnAdd);
        gb.Controls.Add(_btnRemoveUser);
        gb.Height = y + 31 + 25 + 14;
        return gb;
    }

    private GroupBox BuildCameraGroup()
    {
        var gb = NewGroup("Câmera e arquivos");
        int y = 12;

        _cboCamera = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 200,
            Location = new Point(12, y),
        };
        for (int i = 0; i < 10; i++) _cboCamera.Items.Add($"Câmera {i}");
        _cboCamera.SelectedIndexChanged += (s, e) =>
        {
            if (_loadingUi || _cboCamera.SelectedIndex < 0) return;
            if (_config.CameraIndex != _cboCamera.SelectedIndex)
            {
                _config.CameraIndex = _cboCamera.SelectedIndex;
                SaveConfig();
                _engine.Restart();
            }
        };

        _btnTestCamera = new Button
        {
            Text = "Testar",
            Width = 102,
            Height = 25,
            FlatStyle = FlatStyle.System,
            Location = new Point(222, y - 1),
        };
        _btnTestCamera.Click += (s, e) => TestCamera();

        gb.Controls.Add(_cboCamera);
        gb.Controls.Add(_btnTestCamera);
        y += 34;

        AddLabel(gb, "Pasta onde as fotos são salvas:", 12, y);
        y += 20;

        _txtOutput = new TextBox
        {
            ReadOnly = true,
            Width = 228,
            Location = new Point(12, y),
        };
        var btnBrowse = new Button
        {
            Text = "...",
            Width = 30,
            Height = 25,
            FlatStyle = FlatStyle.System,
            Location = new Point(246, y - 1),
        };
        btnBrowse.Click += (s, e) => BrowseOutput();

        _btnTestFiles = CreateOpenFolderButton(y);
        _btnTestFiles.Click += (s, e) => OpenOutputFolder();

        gb.Controls.Add(_txtOutput);
        gb.Controls.Add(btnBrowse);
        gb.Controls.Add(_btnTestFiles);
        y += 31;

        gb.Height = y + 6;
        return gb;
    }

    private GroupBox BuildGeneralGroup()
    {
        var gb = NewGroup("Geral");
        int y = 10;

        _ckNotifications = NewCheck("Notificar na bandeja ao capturar", 12, y);
        _ckNotifications.CheckedChanged += (s, e) =>
        {
            _config.NotificationsEnabled = _ckNotifications.Checked;
            SaveConfig();
        };
        gb.Controls.Add(_ckNotifications);
        y += 28;

        _ckStartup = NewCheck("Iniciar com o Windows (funciona 24/7)", 12, y);
        _ckStartup.CheckedChanged += (s, e) =>
        {
            _config.StartWithWindows = _ckStartup.Checked;
            WindowsStartupService.SetEnabled(_ckStartup.Checked);
            SaveConfig();
        };
        gb.Controls.Add(_ckStartup);
        y += 34;

        {
            var hint = new Label
            {
                Text = "Feche a janela para continuar rodando na bandeja.\nPara sair de vez, use o menu da bandeja → Sair.\nESPAÇO ou F9 = tirar foto (1 s de previsão), F8 = pausar.",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(12, y),
            };
            gb.Controls.Add(hint);
            y += hint.Height + 10;
        }

        gb.Height = y + 6;
        return gb;
    }

    // Campos auxiliares (declarados separadamente para organizar o BuildCameraGroup)
    private Button _btnTestFiles = null!;

    private Button CreateOpenFolderButton(int y)
    {
        return new Button
        {
            Text = "Abrir pasta",
            Width = 46,
            Height = 25,
            FlatStyle = FlatStyle.System,
            Location = new Point(282, y - 1),
            Padding = new Padding(0),
            Font = new Font(Font.FontFamily, 7.5f),
        };
    }

    private static GroupBox NewGroup(string text) => new()
    {
        Text = text,
        Width = SettingsGroupWidth - 6,
        Padding = new Padding(11, 4, 11, 6),
        // Título claro: sem isso o GroupBox usa o cinza-escuro padrão do Windows
        // e some contra o fundo escuro da página (a página ficava "back escuro,
        // letra escura também" — ilegível).
        ForeColor = Color.FromArgb(232, 232, 238),
        Font = new Font(Control.DefaultFont.FontFamily, 8.4f, FontStyle.Bold),
    };

    private static Label AddLabel(GroupBox gb, string text, int x, int y)
    {
        var l = new Label { Text = text, AutoSize = true, Location = new Point(x, y) };
        gb.Controls.Add(l);
        return l;
    }

    private static CheckBox NewCheck(string text, int x, int y) =>
        new() { Text = text, AutoSize = true, Location = new Point(x, y) };

    private NumericUpDown AddNumRow(Control host, string label, int x, int y, int min, int max, int step)
    {
        var l = new Label { Text = label, AutoSize = true, Location = new Point(x, y + 2) };
        host.Controls.Add(l);

        var n = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = step,
            Width = 130,
            Location = new Point(x + 170, y),
        };
        host.Controls.Add(n);
        return n;
    }

    // ---------------------------------------------------------------- Bandeja

    private void BuildTray()
    {
        _tray.Icon = AppIcons.Icon;
        _tray.Text = "ScreenLab — rodando";
        _tray.Visible = true;
        _tray.DoubleClick += (s, e) => ShowFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir ScreenLab", null, (s, e) => ShowFromTray());
        menu.Items.Add("Tirar foto (ESPAÇO / F9)", null, (s, e) => RequestPhoto());
        menu.Items.Add(_trayPause);
        menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Iniciar com o Windows");
        startup.Click += (s, e) =>
        {
            bool value = !WindowsStartupService.IsEnabled();
            WindowsStartupService.SetEnabled(value);
            _config.StartWithWindows = value;
            SaveConfig();
            _ckStartup.Checked = value;
        };
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (s, e) => ExitApplication());

        _tray.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        _isHiddenToTray = false;
        WindowState = FormWindowState.Normal;
        Show();
        ShowInTaskbar = true;
        Activate();
        _engine.SetPreviewWanted(true);
    }

    private void HideToTray()
    {
        // Primeiro desliga a pré-visualização p/ economia de CPU, depois esconde.
        _engine.SetPreviewWanted(false);
        _isHiddenToTray = true;
        Hide();
        ShowInTaskbar = false;

        if (_config.NotificationsEnabled)
            _tray.ShowBalloonTip(2000, "ScreenLab",
                "Continuando a captura em segundo plano. Para sair: menu da bandeja → Sair.",
                ToolTipIcon.Info);
    }

    private void TogglePause()
    {
        _engine.TogglePaused();
        UpdatePauseUi(_engine.IsPaused);
    }

    private void UpdatePauseUi(bool paused)
    {
        _btnPause.Text = paused ? "Retomar (F8)" : "Pausar (F8)";
        _trayPause.Text = paused ? "Retomar" : "Pausar";
        _btnCapture.Enabled = !paused     ;
        // Feedback visual da pausa: âmbar pausado, vermelho em execução parada.
        _led.SetIdle(paused
            ? Color.FromArgb(240, 165, 30)   // âmbar: pausado
            : Color.FromArgb(230, 45, 45));  // vermelho: executando sem captura
    }

    /// <summary>
    /// Inicia a captura manual com 1 s de previsão (mostra o "1" na tela).
    /// Chamado pelo botão, pela tecla ESPAÇO e pelo F9.
    /// </summary>
    private void RequestPhoto()
    {
        if (_countdownActive || !_engine.IsRunning || _engine.IsPaused)
            return;

        _countdownActive = true;
        _lblHeaderStatus.Text = "Preparando foto (1 s)...";
        _lblCountdown.Text = "1";
        _lblCountdown.Visible = true;
        _led.SetIdle(Color.FromArgb(230, 45, 45)); // vermelho até o disparo

        _ = DoCaptureAfterDelay();
    }

    private async Task DoCaptureAfterDelay()
    {
        try
        {
            await Task.Delay(1000);
            _engine.CaptureNow("Manual");
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha durante a contagem para a foto", ex);
        }
        finally
        {
            _lblCountdown.Visible = false;
            _lblHeaderStatus.Text = _engine.IsPaused ? "Pausado" : "Em execução";
            _countdownActive = false;
        }
    }

    private void CenterCountdown()
    {
        if (_preview == null || _lblCountdown == null)
            return;
        int w = Math.Max(160, _preview.ClientSize.Width / 2);
        int h = Math.Max(110, _preview.ClientSize.Height / 3);
        _lblCountdown.Size = new Size(w, h);
        _lblCountdown.Location = new Point(
            (_preview.ClientSize.Width - w) / 2,
            (_preview.ClientSize.Height - h) / 2);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // ESPAÇO sempre fotografa (mesmo com foco num botão), exceto
        // quando o foco está num campo de digitação de texto.
        if (e.KeyCode == Keys.Space)
        {
            bool typing = ActiveControl is TextBox
                          || (ActiveControl is ComboBox { DroppedDown: true });
            if (!typing)
            {
                RequestPhoto();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        }
        else if (e.KeyCode == Keys.Escape && _settingsOpen)
        {
            CloseSettingsPage();
        }
    }

    private void UpdateRunningUi(bool running)
    {
        _btnCapture.Enabled = running && !_engine.IsPaused;
        _btnPause.Enabled = running;
        _cboCamera.Enabled = running;
        _btnTestCamera.Enabled = true;
        if (running)
        {
            _lblStatus.Text = _engine.IsPaused ? "Pausado" : "Em execução";
        }
        else
        {
            _led.SetIdle(Color.FromArgb(230, 45, 45));
            _lblHeaderStatus.Text = "Parado";
        }
    }

    private void TestCamera()
    {
        int index = _cboCamera.SelectedIndex;
        _btnTestCamera.Enabled = false;
        _lblStatus.Text = $"Testando câmera {index}...";
        try
        {
            string info = CaptureEngine.DescribeCamera(index);
            bool ok = CaptureEngine.CameraAvailable(index);
            if (ok && info != "indisponível")
            {
                _lblStatus.Text = $"Câmera {index} OK — {info}";
                LoggerService.Info($"Câmera {index} testada: {info}");
            }
            else
            {
                _lblStatus.Text = $"Câmera {index} indisponível. Verifique conexão e drivers.";
                LoggerService.Warn($"Câmera {index} indisponível no teste");
            }
        }
        finally
        {
            _btnTestCamera.Enabled = true;
        }
    }

    private void BrowseOutput()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Selecione a pasta para salvar as fotos",
            SelectedPath = _config.OutputFolder,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _config.OutputFolder = dlg.SelectedPath;
            _txtOutput.Text = dlg.SelectedPath;
            SaveConfig();
        }
    }

    private void OpenOutputFolder()
    {
        string dir = _config.OutputFolder;
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao abrir pasta de fotos", ex);
        }
    }

    private void AddNewUser()
    {
        string name = _txtNewUser.Text.Trim();
        if (string.IsNullOrEmpty(name) || name == "Novo usuário...")
        {
            _txtNewUser.Focus();
            return;
        }
        _users.AddUser(name);
        _txtNewUser.Clear();
        _txtNewUser.ForeColor = Color.Gray;
    }

    private void RemoveSelectedUser()
    {
        if (_cboUser.SelectedItem is not string name) return;
        if (MessageBox.Show(this, $"Remover o usuário \"{name}\"?",
                "ScreenLab", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _users.RemoveUser(name);
    }

    // ---------------------------------------------------------------- Config ↔ UI

    private void LoadConfigIntoUi()
    {
        _loadingUi = true;
        try
        {
            ReloadUserCombo();

            _numCooldown.Value = Math.Clamp(_config.CooldownSeconds, 0, 3600);

            _cboCamera.SelectedIndex = Math.Clamp(_config.CameraIndex, 0, 9);
            _txtOutput.Text = _config.OutputFolder;

            _ckNotifications.Checked = _config.NotificationsEnabled;
            _ckStartup.Checked = WindowsStartupService.IsEnabled();

            UpdatePauseUi(false);
            UpdateRunningUi(true);
            RebuildOperatorButtons(); // monta os botões grandes dos operadores
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void ReloadUserCombo()
    {
        string active = _users.ActiveUser;
        _cboUser.Items.Clear();
        _cboUser.Items.AddRange(_users.Users.ToArray());
        if (_users.Users.Contains(active))
            _cboUser.SelectedItem = active;
        else if (_cboUser.Items.Count > 0)
            _cboUser.SelectedIndex = 0;

        _btnRemoveUser.Enabled = _cboUser.Items.Count > 1;
    }

    private void SaveConfig() => ConfigService.Save(_config);

    // ---------------------------------------------------------------- Motor

    private void WireEngineEvents()
    {
        _engine.PreviewFrame += OnPreviewFrame;
        _engine.PhotoCaptureStarted += trigger => SafeBeginInvoke(() =>
        {
            _lblHeaderStatus.Text = "Capturando...";
            _led.SetIdle(Color.FromArgb(0, 220, 80)); // verde: tirando a foto
        });
        _engine.PhotoCaptured += OnPhotoCaptured;
        _engine.StatusChanged += s => SafeBeginInvoke(() => ApplyStatus(s));
        _engine.RunningChanged += running => SafeBeginInvoke(() => UpdateRunningUi(running));
        _engine.ErrorOccurred += msg => SafeBeginInvoke(() =>
        {
            _lblStatus.Text = "Erro: " + msg;
            _lblHeaderStatus.Text = "Erro";
            _led.SetIdle(Color.FromArgb(230, 45, 45)); // vermelho: sem captura
        });
    }

    private void ApplyStatus(string status)
    {
        if (string.IsNullOrEmpty(status))
            return;

        _lblStatus.Text = status;
        _lblHeaderStatus.Text = status;
        _led.SetIdle(Color.FromArgb(230, 45, 45)); // vermelho: não está tirando foto
    }

    private void OnPreviewFrame(Mat frame)
    {
        if (_isHiddenToTray || IsDisposed || Disposing)
            return;

        System.Drawing.Bitmap bmp;
        try
        {
            bmp = MatToBitmap(frame);
        }
        catch
        {
            return;
        }

        SafeBeginInvoke(() =>
        {
            if (IsDisposed || Disposing)
            {
                bmp.Dispose();
                return;
            }
            var old = _preview.Image;
            _preview.Image = bmp;
            old?.Dispose();
        });
    }

    private void OnPhotoCaptured(CapturedPhoto photo)
    {
        SafeBeginInvoke(() =>
        {
            _lblLast.Text = $"Última foto: {photo.Timestamp:HH:mm:ss} • {photo.User} • {photo.Trigger}";
            _lblHeaderLast.Text = $"Última foto: {photo.Timestamp:HH:mm:ss} • {photo.User}";
            _led.SetIdle(Color.FromArgb(230, 45, 45)); // volta ao vermelho: captura concluída
            if (_isHiddenToTray && _config.NotificationsEnabled)
            {
                _tray.ShowBalloonTip(2000, "ScreenLab — Foto capturada",
                    $"{photo.User} às {photo.Timestamp:HH:mm:ss}\n{photo.FileName}",
                    ToolTipIcon.Info);
            }
        });
    }

    /// <summary>
    /// Converte Mat (BGR) para Bitmap com cópia direta de linhas — muito mais
    /// rápido e leve que encode BMP + cópia. Ideal para pré-visualização em 24/7.
    /// </summary>
    private static Bitmap MatToBitmap(Mat mat)
    {
        Mat? converted = null;
        if (mat.Channels() == 4)
        {
            converted = new Mat();
            Cv2.CvtColor(mat, converted, ColorConversionCodes.BGRA2BGR);
            mat = converted;
        }

        try
        {
            int w = mat.Width;
            int h = mat.Height;
            int cb = w * 3;
            int srcStep = (int)mat.Step();

            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                // Buffer de linha reutilizado (thread do motor) → sem alocação por frame.
                if (_rowBuffer == null || _rowBuffer.Length < srcStep)
                    _rowBuffer = new byte[srcStep];

                long srcBase = mat.Data.ToInt64();
                long dstBase = data.Scan0.ToInt64();
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(srcBase + (long)y * srcStep), _rowBuffer, 0, srcStep);
                    Marshal.Copy(_rowBuffer, 0, new IntPtr(dstBase + (long)y * data.Stride), cb);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    [ThreadStatic] private static byte[]? _rowBuffer;

    /// <summary>PictureBox com double-buffering para pré-visualização sem flicker.</summary>
    private sealed class BufferedPictureBox : PictureBox
    {
        public BufferedPictureBox()
        {
            DoubleBuffered = true;
        }
    }

    private void SafeBeginInvoke(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(action);
        }
        catch
        {
            // Janela fechando.
        }
    }

    // ---------------------------------------------------------------- Hotkeys

    private void RegisterHotkeys()
    {
        if (_hotkeysRegistered || !IsHandleCreated)
            return;
        _hotkeysRegistered = RegisterHotKey(Handle, HOTKEY_ID_CAPTURE, MOD_NOREPEAT, (uint)Keys.F9);
        _hotkeysRegistered = RegisterHotKey(Handle, HOTKEY_ID_PAUSE, MOD_NOREPEAT, (uint)Keys.F8);
    }

    private void UnregisterHotkeys()
    {
        if (!_hotkeysRegistered)
            return;
        UnregisterHotKey(Handle, HOTKEY_ID_CAPTURE);
        UnregisterHotKey(Handle, HOTKEY_ID_PAUSE);
        _hotkeysRegistered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HOTKEY_ID_CAPTURE:
                    RequestPhoto();
                    break;
                case HOTKEY_ID_PAUSE:
                    TogglePause();
                    break;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RegisterHotkeys();
        // Garante o tamanho correto da página de configurações: a janela já nasce
        // com o tamanho final (não dispara Resize ao exibir), mas o handle só
        // existe aqui — antes disso o PositionSettingsPage seria ignorado.
        PositionSettingsPage();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
            HideToTray();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        UnregisterHotkeys();
        _tray.Visible = false;
        _tray.Dispose();
        _engine.Dispose();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
        Application.Exit();
    }
}