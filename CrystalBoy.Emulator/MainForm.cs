﻿using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using CrystalBoy.Core;
using CrystalBoy.Emulation;
using CrystalBoy.Emulator.Properties;
using System.Collections.Generic;
using System.Reflection;
using System.ComponentModel;
using CrystalBoy.Emulation.Windows.Forms;
using System.Threading;
using System.Diagnostics;
using CrystalBoy.Emulator.Joypads;

namespace CrystalBoy.Emulator
{
	partial class MainForm : DpiAwareForm
	{
		private static readonly long SpeedUpdateTicks = Stopwatch.Frequency / 10;

		private readonly SynchronizationContext synchronizationContext;
		private readonly Stopwatch speedUpdateStopwatch;
		private DebuggerForm debuggerForm;
		private TileViewerForm tileViewerForm;
		private MapViewerForm mapViewerForm;
		private RomInformationForm romInformationForm;
		private EmulatedGameBoy emulatedGameBoy;
		private ControlVideoRenderer videoRenderer;
		private AudioRenderer audioRenderer;
		private Dictionary<Type, ToolStripMenuItem> joypadPluginMenuItemDictionary;
		private Dictionary<Type, ToolStripMenuItem> videoRendererMenuItemDictionary;
		private Dictionary<Type, ToolStripMenuItem> audioRendererMenuItemDictionary;
		private BinaryWriter ramSaveWriter;
		private BinaryReader ramSaveReader;
		private bool pausedTemporarily;

		#region Constructor and Initialization

		public MainForm()
		{
			synchronizationContext = SynchronizationContext.Current;
			speedUpdateStopwatch = Stopwatch.StartNew();
			InitializeComponent();
			ScaleStatusStrip(CurrentAutoScaleDimensions.Height / 96F); // In DPI scaling mode, CurrentAutoScaleDimensions is the current screen DPI.
			if (components == null) components = new System.ComponentModel.Container();
			emulatedGameBoy = new EmulatedGameBoy(components);
			emulatedGameBoy.TryUsingBootRom = Settings.Default.UseBootstrapRom;
			emulatedGameBoy.EnableFramerateLimiter = Settings.Default.LimitSpeed;
			emulatedGameBoy.RomChanged += OnRomChanged;
			emulatedGameBoy.AfterReset += OnAfterReset;
			emulatedGameBoy.EmulationStatusChanged += OnEmulationStatusChanged;
			emulatedGameBoy.NewFrame += OnNewFrame;
			emulatedGameBoy.BorderChanged += OnBorderChanged;
			try { emulatedGameBoy.Reset(Settings.Default.HardwareType); }
			catch (ArgumentOutOfRangeException) { Settings.Default.HardwareType = emulatedGameBoy.HardwareType; }
			AdjustSize(Settings.Default.ContentSize);
			UpdateEmulationStatus();
			UpdateSpeed();
			SetStatusTextHandler();
			CreateRendererMenuItems();
		}

		/// <summary>Programatically scale the <see cref="StatusStrip"/> control and its child.</summary>
		/// <remarks>This is a hack, because this control really sucks.</remarks>
		/// <param name="factor">The scaling factor to apply.</param>
		private void ScaleStatusStrip(float factor)
		{
			statusStrip.Height = (int)(statusStrip.Height * factor);
			toolStripStatusLabel.Text = "DPI";
			emulationStatusToolStripStatusLabel.Width = (int)(emulationStatusToolStripStatusLabel.Width * factor);
			speedToolStripStatusLabel.Width = (int)(speedToolStripStatusLabel.Width * factor);
			toolStripStatusLabel.Text = string.Empty;
		}

		private void CreateRendererMenuItems()
		{
			joypadPluginMenuItemDictionary = new Dictionary<Type, ToolStripMenuItem>();
			videoRendererMenuItemDictionary = new Dictionary<Type, ToolStripMenuItem>();
			audioRendererMenuItemDictionary = new Dictionary<Type, ToolStripMenuItem>();

			foreach (var plugin in Program.PluginCollection)
			{
				Dictionary<Type, ToolStripMenuItem> menuItemDictionary;
				ToolStripMenuItem pluginListMenuItem;
				EventHandler pluginSelectionHandler;

				switch (plugin.Kind)
				{
					case PluginKind.Joypad:
						menuItemDictionary = joypadPluginMenuItemDictionary;
						pluginListMenuItem = joypadToolStripMenuItem;
						pluginSelectionHandler = OnJoypadPluginMenuItemClick;
						break;
					case PluginKind.Video:
						menuItemDictionary = videoRendererMenuItemDictionary;
						pluginListMenuItem = videoRendererToolStripMenuItem;
						pluginSelectionHandler = OnVideoRendererMenuItemClick;
						break;
					case PluginKind.Audio:
						menuItemDictionary = audioRendererMenuItemDictionary;
						pluginListMenuItem = audioRendererToolStripMenuItem;
						pluginSelectionHandler = OnAudioRendererMenuItemClick;
						break;
					default: // Skip unknown plugin types.
						continue;
				}

				var rendererMenuItem = new ToolStripMenuItem(plugin.DisplayName);

				rendererMenuItem.Click += pluginSelectionHandler;
				rendererMenuItem.ToolTipText = plugin.Description;
				rendererMenuItem.Tag = plugin.Type;

				menuItemDictionary.Add(plugin.Type, rendererMenuItem);
				pluginListMenuItem.DropDownItems.Add(rendererMenuItem);
			}
		}

		private void SetStatusTextHandler()
		{
			foreach (ToolStripItem item in mainMenuStrip.Items)
				SetStatusTextHandler(item);
		}

		private void SetStatusTextHandler(ToolStripItem toolStripItem)
		{
			toolStripItem.MouseEnter += new EventHandler(toolStripItem_MouseEnter);
			toolStripItem.MouseLeave += new EventHandler(toolStripItem_MouseLeave);

			if (toolStripItem is ToolStripDropDownItem)
			{
				ToolStripDropDownItem dropDownItem = (ToolStripDropDownItem)toolStripItem;

				foreach (ToolStripItem item in dropDownItem.DropDownItems)
					SetStatusTextHandler(item);
			}
		}

		#region Audio Renderer Management

		private AudioRenderer CreateAudioRenderer(Type rendererType)
		{
			Type[] constructorParameterTypes;

			// Assume the plugin filter has been passed at loading (it should have !)
			if (rendererType.IsGenericType)
				rendererType = rendererType.MakeGenericType(constructorParameterTypes = new[] { typeof(Control) });
			else if (rendererType.IsSubclassOf(typeof(AudioRenderer<Control>)))
				constructorParameterTypes = new[] { typeof(Control) };
			else if (rendererType.IsSubclassOf(typeof(AudioRenderer<IWin32Window>)))
				constructorParameterTypes = new[] { typeof(IWin32Window) };
			else constructorParameterTypes = Type.EmptyTypes;

			// Not using Activator.CreateInstance here because it doesn't allow to check the exact signature… (Does it really matters ? Somehow…)
			ConstructorInfo constructor = rendererType.GetConstructor(constructorParameterTypes);

			return (AudioRenderer)constructor.Invoke(constructorParameterTypes.Length > 0 ? new[] { toolStripContainer.ContentPanel } : new object[0]);
		}

		private void SwitchAudioRenderer(Type rendererType)
		{
			if (audioRenderer != null)
			{
				emulatedGameBoy.Bus.AudioRenderer = null;
				audioRenderer.Dispose();
				audioRenderer = null;
			}

			audioRenderer = CreateAudioRenderer(rendererType);

			ToolStripMenuItem selectedRendererMenuItem = audioRendererMenuItemDictionary[rendererType];

			foreach (ToolStripMenuItem renderMethodMenuItem in audioRendererMenuItemDictionary.Values)
				renderMethodMenuItem.Checked = renderMethodMenuItem == selectedRendererMenuItem;

			// Store the FullName once we know the type of render method to use
			Settings.Default.AudioRenderer = rendererType.ToString(); // Don't use AssemblyQualifiedName for easing updates, though it should be a better choice

			emulatedGameBoy.Bus.AudioRenderer = audioRenderer;
		}

		#endregion

		#region Video Renderer Management

		private ControlVideoRenderer CreateVideoRenderer(Type rendererType) => (ControlVideoRenderer)Activator.CreateInstance(rendererType, new[] { toolStripContainer.ContentPanel });

		private void SwitchVideoRenderer(Type rendererType)
		{
			if (videoRenderer != null)
			{
				emulatedGameBoy.Bus.VideoRenderer = null;
				videoRenderer.Dispose();
				videoRenderer = null;
			}

			videoRenderer = CreateVideoRenderer(rendererType);
			//videoRenderer.Interpolation = false;
			videoRenderer.BorderVisible = Settings.Default.BorderVisibility == BorderVisibility.On || Settings.Default.BorderVisibility == BorderVisibility.Auto && emulatedGameBoy.HasCustomBorder;

			ToolStripMenuItem selectedRendererMenuItem = videoRendererMenuItemDictionary[rendererType];

			foreach (ToolStripMenuItem renderMethodMenuItem in videoRendererMenuItemDictionary.Values)
				renderMethodMenuItem.Checked = renderMethodMenuItem == selectedRendererMenuItem;

			// Store the FullName once we know the type of render method to use
			Settings.Default.VideoRenderer = rendererType.ToString(); // Don't use AssemblyQualifiedName for easing updates, though it should be a better choice

			emulatedGameBoy.Bus.VideoRenderer = videoRenderer;
		}

		#endregion

		#region Joypad Plugin Management

		private ControlFocusedJoypad CreateJoypadPlugin(int joypadIndex, Type pluginType) => (ControlFocusedJoypad)Activator.CreateInstance(pluginType, new object[] { toolStripContainer.ContentPanel, joypadIndex });

		private void SwitchJoypadPlugin(int joypadIndex, Type pluginType)
		{
			var oldJoypad = emulatedGameBoy.Bus.SetJoypad(joypadIndex, null) as IDisposable;
			if (oldJoypad != null) oldJoypad.Dispose();

			emulatedGameBoy.Bus.SetJoypad(joypadIndex, CreateJoypadPlugin(joypadIndex, pluginType));

			var selectedRendererMenuItem = joypadPluginMenuItemDictionary[pluginType];

			foreach (var joypadPluginMenuItem in joypadPluginMenuItemDictionary.Values)
				joypadPluginMenuItem.Checked = joypadPluginMenuItem == selectedRendererMenuItem;

			// Store the FullName once we know the type of render method to use
			Settings.Default.MainJoypadPlugin = pluginType.ToString(); // Don't use AssemblyQualifiedName for easing updates, though it should be a better choice
		}

		#endregion

		private void InitializePlugins()
		{
			string audioRendererName = Settings.Default.AudioRenderer;
			string videoRendererName = Settings.Default.VideoRenderer;
			string joypadPluginName = Settings.Default.MainJoypadPlugin;

			Type firstJoypadPluginType = null;
			Type firstVideoRendererType = null;
			Type firstAudioRendererType = null;

			Type joypadPluginType = null;
			Type videoRendererType = null;
			Type audioRendererType = null;

			// If simple reflexion fails, try using Name and FullName matches (there may be multiple matches in that case but it is better to avoid writing the full AssemblyQualifiedName in initial configurtaion files)
			foreach (var plugin in Program.PluginCollection)
			{
				switch (plugin.Kind)
				{
					case PluginKind.Joypad:
						if (firstJoypadPluginType == null) firstJoypadPluginType = plugin.Type;
						if (plugin.Type.ToString() == joypadPluginName) joypadPluginType = plugin.Type;
						break;
					case PluginKind.Video:
						if (firstVideoRendererType == null) firstVideoRendererType = plugin.Type;
						if (plugin.Type.ToString() == videoRendererName) videoRendererType = plugin.Type;
						break;
					case PluginKind.Audio:
						if (firstAudioRendererType == null) firstAudioRendererType = plugin.Type;
						if (plugin.Type.ToString() == audioRendererName) audioRendererType = plugin.Type;
						break;
					default:
						continue;
				}
			}

			audioRendererType = audioRendererType ?? firstAudioRendererType;
			videoRendererType = videoRendererType ?? firstVideoRendererType;
			joypadPluginType = joypadPluginType ?? firstJoypadPluginType;

			if (audioRendererType != null) SwitchAudioRenderer(audioRendererType);
			if (videoRendererType != null) SwitchVideoRenderer(videoRendererType);
			if (joypadPluginType != null) SwitchJoypadPlugin(0, joypadPluginType);
		}

		#endregion

		#region Utility Forms

		private DebuggerForm DebuggerForm
		{
			get
			{
				if (debuggerForm == null)
					debuggerForm = new DebuggerForm(emulatedGameBoy);
				return debuggerForm;
			}
		}

		private TileViewerForm TileViewerForm
		{
			get
			{
				if (tileViewerForm == null)
					tileViewerForm = new TileViewerForm(emulatedGameBoy);
				return tileViewerForm;
			}
		}

		private MapViewerForm MapViewerForm
		{
			get
			{
				if (mapViewerForm == null)
					mapViewerForm = new MapViewerForm(emulatedGameBoy);
				return mapViewerForm;
			}
		}

		private RomInformationForm RomInformationForm
		{
			get
			{
				if (romInformationForm == null)
					romInformationForm = new RomInformationForm(emulatedGameBoy);
				return romInformationForm;
			}
		}

		#endregion

		#region ROM Loading & RAM Saving

		private void UnloadRom()
		{
			emulatedGameBoy.Pause();

			if (ramSaveWriter != null)
			{
				WriteRam();

				ramSaveWriter.Close();
				ramSaveWriter = null;

				if (ramSaveReader != null) ramSaveReader.Close();
				ramSaveReader = null;
			}

			emulatedGameBoy.UnloadRom();
		}

		private void LoadRom(string fileName)
		{
			var romFileInfo = new FileInfo(fileName);

			// Open only existing rom files
			if (!romFileInfo.Exists)
				throw new FileNotFoundException();
			if (romFileInfo.Length < 512)
				throw new InvalidOperationException("ROM files must be at least 512 bytes.");
			if (romFileInfo.Length > 8 * 1024 * 1024)
				throw new InvalidOperationException("ROM files cannot exceed 8MB.");

			emulatedGameBoy.LoadRom(MemoryUtility.ReadFile(romFileInfo, true));

			if (emulatedGameBoy.RomInformation.HasRam && emulatedGameBoy.RomInformation.HasBattery)
			{
				var ramFileInfo = new FileInfo(Path.Combine(romFileInfo.DirectoryName, Path.GetFileNameWithoutExtension(romFileInfo.Name)) + ".sav");

				var ramSaveStream = ramFileInfo.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
				ramSaveStream.SetLength(emulatedGameBoy.Mapper.SavedRamSize + (emulatedGameBoy.RomInformation.HasTimer ? 48 : 0));
				ramSaveStream.Read(emulatedGameBoy.ExternalRam, 0, emulatedGameBoy.Mapper.SavedRamSize);
				ramSaveWriter = new BinaryWriter(ramSaveStream);

				if (emulatedGameBoy.RomInformation.HasTimer)
				{
					var mbc3 = emulatedGameBoy.Mapper as CrystalBoy.Emulation.Mappers.MemoryBankController3;

					if (mbc3 != null)
					{
						var rtcState = mbc3.RtcState;
						ramSaveReader = new BinaryReader(ramSaveStream);

						rtcState.Frozen = true;

						rtcState.Seconds = (byte)ramSaveReader.ReadInt32();
						rtcState.Minutes = (byte)ramSaveReader.ReadInt32();
						rtcState.Hours = (byte)ramSaveReader.ReadInt32();
						rtcState.Days = (short)((byte)ramSaveReader.ReadInt32() + ((byte)ramSaveReader.ReadInt32() << 8));

						rtcState.LatchedSeconds = (byte)ramSaveReader.ReadInt32();
						rtcState.LatchedMinutes = (byte)ramSaveReader.ReadInt32();
						rtcState.LatchedHours = (byte)ramSaveReader.ReadInt32();
						rtcState.LatchedDays = (short)((byte)ramSaveReader.ReadInt32() + ((byte)ramSaveReader.ReadInt32() << 8));

						rtcState.DateTime = new DateTime(1970, 1, 1) + TimeSpan.FromSeconds(ramSaveReader.ReadInt64());

						rtcState.Frozen = false;
					}
				}

				emulatedGameBoy.Mapper.RamUpdated += Mapper_RamUpdated;
			}

			emulatedGameBoy.Run();
		}

		private void Mapper_RamUpdated(object sender, EventArgs e) { if (ramSaveWriter != null) WriteRam(); }

		private void WriteRam()
		{
			ramSaveWriter.Seek(0, SeekOrigin.Begin);
			ramSaveWriter.Write(emulatedGameBoy.ExternalRam, 0, emulatedGameBoy.Mapper.SavedRamSize);
			if (emulatedGameBoy.RomInformation.HasTimer)
			{
				var mbc3 = emulatedGameBoy.Mapper as CrystalBoy.Emulation.Mappers.MemoryBankController3;

				if (mbc3 != null)
				{
					var rtcState = mbc3.RtcState;

					// I'll save the date using the same format as VBA in order to be more compatible, but i originally planned to store it whithout wasting bytes…
					// Luckily enough, it seems we use the same iternal representation… (But there probably is no other way to do it)

					ramSaveWriter.Write((int)rtcState.Seconds & 0xFF);
					ramSaveWriter.Write((int)rtcState.Minutes & 0xFF);
					ramSaveWriter.Write((int)rtcState.Hours & 0xFF);
					ramSaveWriter.Write((int)rtcState.Days & 0xFF);
					ramSaveWriter.Write((rtcState.Days >> 8) & 0xFF);

					ramSaveWriter.Write((int)rtcState.LatchedSeconds & 0xFF);
					ramSaveWriter.Write((int)rtcState.LatchedMinutes & 0xFF);
					ramSaveWriter.Write((int)rtcState.LatchedHours & 0xFF);
					ramSaveWriter.Write((int)rtcState.LatchedDays & 0xFF);
					ramSaveWriter.Write((rtcState.LatchedDays >> 8) & 0xFF);

					ramSaveWriter.Write((long)((rtcState.DateTime - new DateTime(1970, 1, 1)).TotalSeconds));
				}
			}
		}

		#endregion

		#region Size Management

		#region Border Visibility Management

		private void OnAfterReset(object sender, EventArgs e) { SetBorderVisibility(Settings.Default.BorderVisibility == BorderVisibility.On || Settings.Default.BorderVisibility == BorderVisibility.Auto && emulatedGameBoy.HasCustomBorder); }

		private void OnBorderChanged(object sender, EventArgs e) { if (Settings.Default.BorderVisibility == BorderVisibility.Auto) ShowBorder(); }

		private void SetBorderVisibility(bool visible) { if (visible) ShowBorder(); else HideBorder(); }

		private void ShowBorder()
		{
			if (videoRenderer != null && !videoRenderer.BorderVisible)
			{
				var panelSize = toolStripContainer.ContentPanel.ClientSize;

				videoRenderer.BorderVisible = true;
				AdjustSize(panelSize.Width * 256 / 160, panelSize.Height * 224 / 144);
			}
		}

		private void HideBorder()
		{
			if (videoRenderer != null && videoRenderer.BorderVisible)
			{
				var panelSize = toolStripContainer.ContentPanel.ClientSize;

				videoRenderer.BorderVisible = false;
				AdjustSize(panelSize.Width * 160 / 256, panelSize.Height * 144 / 224);
			}
		}

		#endregion

		private void SetZoomFactor(int factor)
		{
			var referenceSize = videoRenderer?.BorderVisible ?? false ? new Size(256, 224) : new Size(160, 144);

			if (factor <= 0) throw new ArgumentOutOfRangeException("factor");
			Settings.Default.ZoomFactor = factor;
			AdjustSize(factor * referenceSize.Width, factor * referenceSize.Height);
		}

		private void AdjustSize(Size requestedSize)
		{
			AdjustSize(requestedSize.Width, requestedSize.Height);
		}

		private void AdjustSize(int requestedWidth, int requestedHeight)
		{
			Size newSize, panelSize;

			// First round
			newSize = ClientSize;
			panelSize = toolStripContainer.ContentPanel.ClientSize;
			newSize.Width = newSize.Width - panelSize.Width + requestedWidth;
			newSize.Height = newSize.Height - panelSize.Height + requestedHeight;

			ClientSize = newSize;

			// Second round
			panelSize = toolStripContainer.ContentPanel.ClientSize;
			if (panelSize.Height != requestedHeight)
			{
				newSize.Height = newSize.Height - panelSize.Height + requestedHeight;

				ClientSize = newSize;
			}
		}

		protected override void OnResizeBegin(EventArgs e) { if (pausedTemporarily = emulatedGameBoy.EmulationStatus == EmulationStatus.Running) emulatedGameBoy.Pause(); }

		protected override void OnResizeEnd(EventArgs e) { if (pausedTemporarily) emulatedGameBoy.Run(); }

		#endregion

		#region Status Updates

		private void UpdateEmulationStatus()
		{
			emulationStatusToolStripStatusLabel.Text = emulatedGameBoy.EmulationStatus == EmulationStatus.Running ? Resources.RunningText : Resources.PausedText;
		}

		private void UpdateSpeed()
		{
			double speed = emulatedGameBoy.EmulatedSpeed;

			if (speed > 0) speedToolStripStatusLabel.Text = "Speed: " + speed.ToString("P0");
			else speedToolStripStatusLabel.Text = "Speed: -";
		}

		#endregion

		#region Event Handling

		protected override void OnShown(EventArgs e)
		{
			InitializePlugins();
			emulatedGameBoy.Bus.VideoRenderer = videoRenderer;
			base.OnShown(e);
		}

		protected override void OnActivated(EventArgs e)
		{
			if (emulatedGameBoy != null && pausedTemporarily) emulatedGameBoy.Run();
			base.OnActivated(e);
		}

		protected override void OnDeactivate(EventArgs e)
		{
			if (emulatedGameBoy != null && (pausedTemporarily = emulatedGameBoy.EmulationStatus == EmulationStatus.Running)) emulatedGameBoy.Pause();
			base.OnDeactivate(e);
		}

		protected override void OnClosing(CancelEventArgs e)
		{
			// Make sure that the emulated system is stopped before closing
			emulatedGameBoy.Pause();
			UnloadRom();
			// Calling DoEvents here will make sure that everything gets executed in order, but it should work without.
			Application.DoEvents();
			base.OnClosing(e);
		}

		protected override void OnClosed(EventArgs e)
		{
			Size renderSize = toolStripContainer.ContentPanel.ClientSize;

			Settings.Default.ContentSize = Settings.Default.BorderVisibility != BorderVisibility.On && (videoRenderer?.BorderVisible ?? false) ? new Size(renderSize.Width * 160 / 256, renderSize.Height * 144 / 224) : renderSize;
			Settings.Default.Save();
			base.OnClosed(e);
		}

		private void OnRomChanged(object sender, EventArgs e) { SetBorderVisibility(Settings.Default.BorderVisibility == BorderVisibility.On || Settings.Default.BorderVisibility == BorderVisibility.Auto && emulatedGameBoy.HasCustomBorder); }

		private void OnEmulationStatusChanged(object sender, EventArgs e) { UpdateEmulationStatus(); }

		private void OnNewFrame(object sender, EventArgs e)
		{
			// This method should only be called from the "Processor" thread.
			// Usage of the stopwatch here is safe.
			if (speedUpdateStopwatch.ElapsedTicks > SpeedUpdateTicks)
			{
				speedUpdateStopwatch.Restart();
				synchronizationContext.Post(state => UpdateSpeed(), null);
			}
		}

		private void toolStripContainer_ContentPanel_Paint(object sender, PaintEventArgs e)
		{
			videoRenderer?.Refresh();
		}

		#region Menus

		#region General

		void toolStripItem_MouseLeave(object sender, EventArgs e)
		{
			toolStripStatusLabel.Text = "";
		}

		private void toolStripItem_MouseEnter(object sender, EventArgs e)
		{
			toolStripStatusLabel.Text = ((ToolStripItem)sender).ToolTipText;
		}

		#endregion

		#region File

		private void openToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (openFileDialog.ShowDialog() == DialogResult.OK)
			{
				UnloadRom();
				LoadRom(openFileDialog.FileName);
			}
		}

		private void exitToolStripMenuItem_Click(object sender, EventArgs e) { Close(); }

		#endregion

		#region Emulation

		private void emulationToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
		{
			pauseToolStripMenuItem.Enabled = emulatedGameBoy.EmulationStatus != EmulationStatus.Stopped;
			resetToolStripMenuItem.Enabled = emulatedGameBoy.EmulationStatus != EmulationStatus.Stopped;
			pauseToolStripMenuItem.Checked = emulatedGameBoy.EmulationStatus == EmulationStatus.Paused;
			limitSpeedToolStripMenuItem.Checked = emulatedGameBoy.EnableFramerateLimiter;
		}

		private void pauseToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (emulatedGameBoy.EmulationStatus == EmulationStatus.Paused) emulatedGameBoy.Run();
			else if (emulatedGameBoy.EmulationStatus == EmulationStatus.Running) emulatedGameBoy.Pause();
		}

		private void runFrameToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (emulatedGameBoy.EmulationStatus == EmulationStatus.Running) emulatedGameBoy.Pause();
			if (emulatedGameBoy.EmulationStatus == EmulationStatus.Paused) emulatedGameBoy.RunFrame();
		}

		private void resetToolStripMenuItem_Click(object sender, EventArgs e) { emulatedGameBoy.Reset(); }

		private void limitSpeedToolStripMenuItem_Click(object sender, EventArgs e)
		{
			Settings.Default.LimitSpeed =
				emulatedGameBoy.EnableFramerateLimiter = limitSpeedToolStripMenuItem.Checked;
		}

		#region Video

		private void videoToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
		{
			//interpolationToolStripMenuItem.Enabled = videoRenderer.SupportsInterpolation;
			//interpolationToolStripMenuItem.Checked = videoRenderer.Interpolation;
		}

		private void OnAudioRendererMenuItemClick(object sender, EventArgs e)
		{
			ToolStripMenuItem renderMethodMenuItem = (ToolStripMenuItem)sender;
		}

		private void OnVideoRendererMenuItemClick(object sender, EventArgs e)
		{
			ToolStripMenuItem renderMethodMenuItem = (ToolStripMenuItem)sender;

			SwitchVideoRenderer((Type)renderMethodMenuItem.Tag);
		}

		private void OnJoypadPluginMenuItemClick(object sender, EventArgs e)
		{
			ToolStripMenuItem renderMethodMenuItem = (ToolStripMenuItem)sender;

			SwitchJoypadPlugin(0, (Type)renderMethodMenuItem.Tag);
		}

		#region Border

		private void borderToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
		{
			var borderVisibility = Settings.Default.BorderVisibility;

			borderAutoToolStripMenuItem.Checked = borderVisibility == BorderVisibility.Auto;
			borderOnToolStripMenuItem.Checked = borderVisibility == BorderVisibility.On;
			borderOffToolStripMenuItem.Checked = borderVisibility == BorderVisibility.Off;
		}

		private void borderAutoToolStripMenuItem_Click(object sender, EventArgs e)
		{
			Settings.Default.BorderVisibility = BorderVisibility.Auto;
			SetBorderVisibility(emulatedGameBoy.HasCustomBorder);
		}

		private void borderOnToolStripMenuItem_Click(object sender, EventArgs e)
		{
			Settings.Default.BorderVisibility = BorderVisibility.On;
			ShowBorder();
		}

		private void borderOffToolStripMenuItem_Click(object sender, EventArgs e)
		{
			Settings.Default.BorderVisibility = BorderVisibility.Off;
			HideBorder();
		}

		#endregion

		#region Zoom

		private void zoomToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
		{
			Size renderSize = toolStripContainer.ContentPanel.ClientSize;
			ToolStripMenuItem checkItem = null;

			if (renderSize == new Size(160, 144))
				checkItem = zoom100toolStripMenuItem;
			else if (renderSize == new Size(320, 288))
				checkItem = zoom200toolStripMenuItem;
			else if (renderSize == new Size(480, 432))
				checkItem = zoom300toolStripMenuItem;
			else if (renderSize == new Size(640, 576))
				checkItem = zoom400toolStripMenuItem;
			else if (renderSize == new Size(960, 864))
				checkItem = zoom600toolStripMenuItem;
			else if (renderSize == new Size(1280, 1152))
				checkItem = zoom800toolStripMenuItem;

			foreach (ToolStripMenuItem zoomMenuItem in zoomToolStripMenuItem.DropDownItems)
				zoomMenuItem.Checked = zoomMenuItem == checkItem;
		}

		private void zoom100toolStripMenuItem_Click(object sender, EventArgs e) => SetZoomFactor(1);
		private void zoom200toolStripMenuItem_Click(object sender, EventArgs e) => SetZoomFactor(2);
		private void zoom300toolStripMenuItem_Click(object sender, EventArgs e) => SetZoomFactor(3);
		private void zoom400toolStripMenuItem_Click(object sender, EventArgs e) => SetZoomFactor(4);
		private void zoom600toolStripMenuItem_Click(object sender, EventArgs e) => SetZoomFactor(6);
		private void zoom800toolStripMenuItem_Click(object sender, EventArgs e) => SetZoomFactor(8);

		#endregion

		private void interpolationToolStripMenuItem_Click(object sender, EventArgs e) { /*videoRenderer.Interpolation = !videoRenderer.Interpolation;*/ }

		#endregion

		#region Hardware

		private void hardwareToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
		{
			useBootstrapRomToolStripMenuItem.Checked = emulatedGameBoy.TryUsingBootRom;
			// The tags have been set by manually editing the designed form code.
			// This should work fine as long as the tag isn't modified in the Windows Forms editor.
			foreach (ToolStripItem item in hardwareToolStripMenuItem.DropDownItems)
				if (item.Tag is HardwareType)
					(item as ToolStripMenuItem).Checked = (HardwareType)item.Tag == emulatedGameBoy.HardwareType;
		}

		private void useBootstrapRomToolStripMenuItem_Click(object sender, EventArgs e)
		{
			Settings.Default.UseBootstrapRom = emulatedGameBoy.TryUsingBootRom = !emulatedGameBoy.TryUsingBootRom;
			if (!emulatedGameBoy.IsRomLoaded || emulatedGameBoy.EmulationStatus == EmulationStatus.Stopped)
				emulatedGameBoy.Reset();
		}

		private void randomHardwareToolStripMenuItem_Click(object sender, EventArgs e)
		{
			var menuItem = sender as ToolStripMenuItem;

			if (menuItem.Tag is HardwareType) SwitchHardware((HardwareType)menuItem.Tag);
			else throw new InvalidOperationException();
		}

		private void SwitchHardware(HardwareType hardwareType)
		{
			if (!emulatedGameBoy.IsRomLoaded || MessageBox.Show(this, Resources.EmulationResetMessage, Resources.GenericMessageTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
				emulatedGameBoy.Reset(Settings.Default.HardwareType = hardwareType);
		}

		#endregion

		#endregion

		#region Tools

		private void romInformationToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (!RomInformationForm.Visible)
				RomInformationForm.Show(this);
		}

		private void debuggerToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (!DebuggerForm.Visible)
				DebuggerForm.Show(this);
		}

		private void tileViewerToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (!TileViewerForm.Visible)
				TileViewerForm.Show(this);
		}

		private void mapViewerToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (!MapViewerForm.Visible)
				MapViewerForm.Show(this);
		}

		#endregion

		#region Help

		private void aboutToolStripMenuItem_Click(object sender, EventArgs e) { AboutForm.Default.ShowDialog(this); }

		#endregion

		#endregion

		#endregion
	}
}
