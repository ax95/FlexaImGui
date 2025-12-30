#define INCLUDE_IMGUIZMO
#define INCLUDE_IMNODES
#define INCLUDE_IMPLOT
#define INCLUDE_IMWIDGETS

using System;
using System.IO;
using System.Runtime.InteropServices;
using FlaxEngine;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
#if INCLUDE_IMGUIZMO
using Hexa.NET.ImGuizmo;
#endif
#if INCLUDE_IMNODES
using Hexa.NET.ImNodes;
#endif
#if INCLUDE_IMPLOT
using Hexa.NET.ImPlot;
#endif

namespace FlexaPlugin;

// TODO:
// - Get the span working with Render2D.DrawTexturedTriangles()
// - Get custom automatic imgui.ini saving to work without crashing
// Waiting for the ability to embed things into the .net executable (like .ttf files)
// Also, upcoming nuget support that will potentially nullify all the effort put into Hexa.NET.ImGui lib loading
// Also also, waiting for ImGuizmo to be fixed with the latest version of Hexa.NET.ImGui
// Also also also, implement and support ImPlot3D which is now included with prerelease of Hexa.NET.ImGui

/// <summary>
/// The FlexaImGui game plugin.
/// </summary>
/// <seealso cref="FlaxEngine.GamePlugin" />
public class FlexaImGui : GamePlugin
{
	public static FlexaImGui Instance { get; private set; }

	public enum ImGuiStyle
	{
		/// <summary>
		/// Default Dark ImGui style.
		/// </summary>
		Dark,
		/// <summary>
		/// Default Light ImGui style.
		/// </summary>
		Light,
		/// <summary>
		/// Classic ImGui style.
		/// </summary>
		Classic,
		/// <summary>
		/// Custom ImGui style; you'll have to define your own from ImGui.GetStyle().
		/// </summary>
		Custom
	}

	private static ImGuiContextPtr _imGuiContext;
#if INCLUDE_IMNODES
	private static ImNodesContextPtr _imNodesContext;
#endif
#if INCLUDE_IMPLOT
	private static ImPlotContextPtr _imPlotContext;
#endif

	private int _freeOffset, _endOffset;
	private GPUTexture[] _textures = new GPUTexture[1];

	private Float2[] _vertices = [];
	private Float2[] _uvs = [];
	private Color[] _colors = [];
	private ushort[] _indices = [];
	private ushort[] _dynIndices = [];
	private bool _activeFrame;
	
	/// <summary>
	/// Whether the FlexaImGui plugin is enabled.
	/// </summary>
	public bool Enabled = true;
	
	/// <summary>
	/// Whether ImGui input is enabled.
	/// </summary>
	public bool EnableInput = true;
	
	/// <summary>
	/// Whether ImGui drawing is enabled.
	/// </summary>
	public bool EnableDrawing = true;

	/// <summary>
	/// Whether automatic imgui.ini saving/loading is enabled (on init and deinit).
	/// </summary>
	public const bool EnableSaveLoad = true;
	
	/// <summary>
	/// The default ImGuiConfigFlags to use within Setup.
	/// </summary>
	public const ImGuiConfigFlags DefaultConfigFlags = 
		ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;

	/// <summary>
	/// The default ImGuiBackendFlags to use within Setup.
	/// </summary>
	public const ImGuiBackendFlags DefaultBackendFlags =
		ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.HasGamepad |
		ImGuiBackendFlags.HasMouseCursors | ImGuiBackendFlags.RendererHasVtxOffset;
	
	/// <inheritdoc />
	public FlexaImGui()
	{
		_description = new PluginDescription
		{
			Name = "FlexaImGui",
			Category = "GUI",
			Author = "Nooke",
			AuthorUrl = null,
			HomepageUrl = null,
			RepositoryUrl = "https://github.com/FlaxEngine/ExamplePlugin",
			Description = "Hexa.NET.ImGui plugin for Flax.",
			Version = new Version(1, 0, 0),
			IsAlpha = false,
			IsBeta = false,
		};
	}

	/// <inheritdoc />
	public override void @Initialize()
	{
		base.Initialize();

		// Bind update events
		Scripting.Update += OnUpdate;
		Scripting.LateUpdate += OnLateUpdate;
		MainRenderTask.Instance.PostRender += OnPostRender;
	}
	
	/// <summary>
	/// Performs ImGui initialization. Must be called before the first update occurs.
	/// </summary>
	/// <param name="style">Pass in the style to load, or use ImGuiStyle.Custom to skip loading any default style.</param>
	/// <param name="configFlags">The configuration flags to use.</param>
	/// <param name="backendFlags">The backend flags to use.</param>
	public static unsafe void Setup(ImGuiStyle style = ImGuiStyle.Dark, ImGuiConfigFlags configFlags = DefaultConfigFlags, 
		ImGuiBackendFlags backendFlags = DefaultBackendFlags)
	{
		// Initialize
		LibraryLoader.InterceptLibraryLoad += InterceptLibraryLoad;
		_imGuiContext = ImGui.CreateContext();
		ImGuiIOPtr io = ImGui.GetIO();
		io.ConfigFlags = configFlags;
		io.BackendFlags = backendFlags;
		if (!EnableSaveLoad)
			io.IniFilename = null;
		
#if INCLUDE_IMGUIZMO
		ImGuizmo.SetImGuiContext(_imGuiContext);
#endif
#if INCLUDE_IMNODES
		_imNodesContext = ImNodes.CreateContext();
		ImNodes.SetImGuiContext(_imGuiContext);
#endif
#if INCLUDE_IMPLOT
		_imPlotContext = ImPlot.CreateContext();
		ImPlot.SetImGuiContext(_imGuiContext);
#endif
#if INCLUDE_IMWIDGETS
		Hexa.NET.ImGui.Widgets.WidgetManager.Init();
#endif
		
		// Set style
		switch (style)
		{
			case ImGuiStyle.Dark:
				ImGui.StyleColorsDark();
				break;
			case ImGuiStyle.Light:
				ImGui.StyleColorsLight();
				break;
			case ImGuiStyle.Classic:
				ImGui.StyleColorsClassic();
				break;
		}
		
		// Handle DPI
		var stylePtr = ImGui.GetStyle();
		stylePtr.ScaleAllSizes(Platform.DpiScale);
		stylePtr.FontScaleDpi = Platform.DpiScale;
		io.ConfigDpiScaleFonts = true;
	}

	private void OnUpdate()
	{
		if (!Enabled)
		{
			_activeFrame = false;
			return;
		}
		
		// Begin frame
		_activeFrame = true;
		ImGuiIOPtr io = ImGui.GetIO();
		io.DeltaTime = Time.UnscaledDeltaTime;

		// Update screen size
		io.DisplaySize.X = Screen.Size.X;
		io.DisplaySize.Y = Screen.Size.Y;
#if INCLUDE_IMGUIZMO
		ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
#endif
		
		bool hasFocus = Engine.HasGameViewportFocus && EnableInput;
		io.AddFocusEvent(hasFocus);
		if (hasFocus)
		{
			// Mouse and text input events
			var mousePos = Input.MousePosition;
			io.AddMousePosEvent(mousePos.X, mousePos.Y);
			io.AddMouseButtonEvent(0, Input.GetMouseButton(MouseButton.Left));
			io.AddMouseButtonEvent(1, Input.GetMouseButton(MouseButton.Right));
			io.AddMouseButtonEvent(2, Input.GetMouseButton(MouseButton.Middle));
			io.AddMouseWheelEvent(0, Input.MouseScrollDelta);
			var inputText = Input.InputText;
			if (inputText.Length != 0)
			{
				foreach (var ch in Input.InputText)
					io.AddInputCharacterUTF16(ch);
			}

			// Keyboard mappings
			io.AddKeyEvent(ImGuiKey.Tab, Input.Keyboard.GetKey(KeyboardKeys.Tab));
			io.AddKeyEvent(ImGuiKey.LeftArrow, Input.Keyboard.GetKey(KeyboardKeys.ArrowLeft));
			io.AddKeyEvent(ImGuiKey.RightArrow , Input.Keyboard.GetKey(KeyboardKeys.ArrowRight));
			io.AddKeyEvent(ImGuiKey.UpArrow , Input.Keyboard.GetKey(KeyboardKeys.ArrowUp));
			io.AddKeyEvent(ImGuiKey.DownArrow , Input.Keyboard.GetKey(KeyboardKeys.ArrowDown));
			io.AddKeyEvent(ImGuiKey.PageUp , Input.Keyboard.GetKey(KeyboardKeys.PageUp));
			io.AddKeyEvent(ImGuiKey.PageDown , Input.Keyboard.GetKey(KeyboardKeys.PageDown));
			io.AddKeyEvent(ImGuiKey.Home, Input.Keyboard.GetKey(KeyboardKeys.Home));
			io.AddKeyEvent(ImGuiKey.End, Input.Keyboard.GetKey(KeyboardKeys.End));
			io.AddKeyEvent(ImGuiKey.Insert, Input.Keyboard.GetKey(KeyboardKeys.Insert));
			io.AddKeyEvent(ImGuiKey.Delete, Input.Keyboard.GetKey(KeyboardKeys.Delete));
			io.AddKeyEvent(ImGuiKey.Backspace, Input.Keyboard.GetKey(KeyboardKeys.Backspace));
			io.AddKeyEvent(ImGuiKey.Space, Input.Keyboard.GetKey(KeyboardKeys.Spacebar));
			io.AddKeyEvent(ImGuiKey.Enter, Input.Keyboard.GetKey(KeyboardKeys.Return));
			io.AddKeyEvent(ImGuiKey.Escape, Input.Keyboard.GetKey(KeyboardKeys.Escape));
			io.AddKeyEvent(ImGuiKey.LeftCtrl, Input.Keyboard.GetKey(KeyboardKeys.Control));
			io.AddKeyEvent(ImGuiKey.LeftShift, Input.Keyboard.GetKey(KeyboardKeys.Shift));
			io.AddKeyEvent(ImGuiKey.LeftAlt, Input.Keyboard.GetKey(KeyboardKeys.Alt));
			io.AddKeyEvent(ImGuiKey.LeftSuper, Input.Keyboard.GetKey(KeyboardKeys.LeftWindows));
			io.AddKeyEvent(ImGuiKey.RightCtrl, Input.Keyboard.GetKey(KeyboardKeys.Control));
			io.AddKeyEvent(ImGuiKey.RightShift, Input.Keyboard.GetKey(KeyboardKeys.Shift));
			io.AddKeyEvent(ImGuiKey.RightAlt, Input.Keyboard.GetKey(KeyboardKeys.Alt));
			io.AddKeyEvent(ImGuiKey.RightSuper, Input.Keyboard.GetKey(KeyboardKeys.RightWindows));
			io.AddKeyEvent(ImGuiKey.Menu, Input.Keyboard.GetKey(KeyboardKeys.LeftMenu));
			io.AddKeyEvent(ImGuiKey.Key0, Input.Keyboard.GetKey(KeyboardKeys.Alpha0));
			io.AddKeyEvent(ImGuiKey.Key1, Input.Keyboard.GetKey(KeyboardKeys.Alpha1));
			io.AddKeyEvent(ImGuiKey.Key2, Input.Keyboard.GetKey(KeyboardKeys.Alpha2));
			io.AddKeyEvent(ImGuiKey.Key3, Input.Keyboard.GetKey(KeyboardKeys.Alpha3));
			io.AddKeyEvent(ImGuiKey.Key4, Input.Keyboard.GetKey(KeyboardKeys.Alpha4));
			io.AddKeyEvent(ImGuiKey.Key5, Input.Keyboard.GetKey(KeyboardKeys.Alpha5));
			io.AddKeyEvent(ImGuiKey.Key6, Input.Keyboard.GetKey(KeyboardKeys.Alpha6));
			io.AddKeyEvent(ImGuiKey.Key7, Input.Keyboard.GetKey(KeyboardKeys.Alpha7));
			io.AddKeyEvent(ImGuiKey.Key8, Input.Keyboard.GetKey(KeyboardKeys.Alpha8));
			io.AddKeyEvent(ImGuiKey.Key9, Input.Keyboard.GetKey(KeyboardKeys.Alpha9));
			io.AddKeyEvent(ImGuiKey.A, Input.Keyboard.GetKey(KeyboardKeys.A));
			io.AddKeyEvent(ImGuiKey.B, Input.Keyboard.GetKey(KeyboardKeys.B));
			io.AddKeyEvent(ImGuiKey.C, Input.Keyboard.GetKey(KeyboardKeys.C));
			io.AddKeyEvent(ImGuiKey.D, Input.Keyboard.GetKey(KeyboardKeys.D));
			io.AddKeyEvent(ImGuiKey.E, Input.Keyboard.GetKey(KeyboardKeys.E));
			io.AddKeyEvent(ImGuiKey.F, Input.Keyboard.GetKey(KeyboardKeys.F));
			io.AddKeyEvent(ImGuiKey.G, Input.Keyboard.GetKey(KeyboardKeys.G));
			io.AddKeyEvent(ImGuiKey.H, Input.Keyboard.GetKey(KeyboardKeys.H));
			io.AddKeyEvent(ImGuiKey.I, Input.Keyboard.GetKey(KeyboardKeys.I));
			io.AddKeyEvent(ImGuiKey.J, Input.Keyboard.GetKey(KeyboardKeys.J));
			io.AddKeyEvent(ImGuiKey.K, Input.Keyboard.GetKey(KeyboardKeys.K));
			io.AddKeyEvent(ImGuiKey.L, Input.Keyboard.GetKey(KeyboardKeys.L));
			io.AddKeyEvent(ImGuiKey.M, Input.Keyboard.GetKey(KeyboardKeys.M));
			io.AddKeyEvent(ImGuiKey.N, Input.Keyboard.GetKey(KeyboardKeys.N));
			io.AddKeyEvent(ImGuiKey.O, Input.Keyboard.GetKey(KeyboardKeys.O));
			io.AddKeyEvent(ImGuiKey.P, Input.Keyboard.GetKey(KeyboardKeys.P));
			io.AddKeyEvent(ImGuiKey.Q, Input.Keyboard.GetKey(KeyboardKeys.Q));
			io.AddKeyEvent(ImGuiKey.R, Input.Keyboard.GetKey(KeyboardKeys.R));
			io.AddKeyEvent(ImGuiKey.S, Input.Keyboard.GetKey(KeyboardKeys.S));
			io.AddKeyEvent(ImGuiKey.T, Input.Keyboard.GetKey(KeyboardKeys.T));
			io.AddKeyEvent(ImGuiKey.U, Input.Keyboard.GetKey(KeyboardKeys.U));
			io.AddKeyEvent(ImGuiKey.V, Input.Keyboard.GetKey(KeyboardKeys.V));
			io.AddKeyEvent(ImGuiKey.W, Input.Keyboard.GetKey(KeyboardKeys.W));
			io.AddKeyEvent(ImGuiKey.X, Input.Keyboard.GetKey(KeyboardKeys.X));
			io.AddKeyEvent(ImGuiKey.Y, Input.Keyboard.GetKey(KeyboardKeys.Y));
			io.AddKeyEvent(ImGuiKey.Z, Input.Keyboard.GetKey(KeyboardKeys.Z));
			io.AddKeyEvent(ImGuiKey.F1, Input.Keyboard.GetKey(KeyboardKeys.F1));
			io.AddKeyEvent(ImGuiKey.F2, Input.Keyboard.GetKey(KeyboardKeys.F2));
			io.AddKeyEvent(ImGuiKey.F3, Input.Keyboard.GetKey(KeyboardKeys.F3));
			io.AddKeyEvent(ImGuiKey.F4, Input.Keyboard.GetKey(KeyboardKeys.F4));
			io.AddKeyEvent(ImGuiKey.F5, Input.Keyboard.GetKey(KeyboardKeys.F5));
			io.AddKeyEvent(ImGuiKey.F6, Input.Keyboard.GetKey(KeyboardKeys.F6));
			io.AddKeyEvent(ImGuiKey.F7, Input.Keyboard.GetKey(KeyboardKeys.F7));
			io.AddKeyEvent(ImGuiKey.F8, Input.Keyboard.GetKey(KeyboardKeys.F8));
			io.AddKeyEvent(ImGuiKey.F9, Input.Keyboard.GetKey(KeyboardKeys.F9));
			io.AddKeyEvent(ImGuiKey.F10, Input.Keyboard.GetKey(KeyboardKeys.F10));
			io.AddKeyEvent(ImGuiKey.F11, Input.Keyboard.GetKey(KeyboardKeys.F11));
			io.AddKeyEvent(ImGuiKey.F12, Input.Keyboard.GetKey(KeyboardKeys.F12));
			io.AddKeyEvent(ImGuiKey.F13, Input.Keyboard.GetKey(KeyboardKeys.F13));
			io.AddKeyEvent(ImGuiKey.F14, Input.Keyboard.GetKey(KeyboardKeys.F14));
			io.AddKeyEvent(ImGuiKey.F15, Input.Keyboard.GetKey(KeyboardKeys.F15));
			io.AddKeyEvent(ImGuiKey.F16, Input.Keyboard.GetKey(KeyboardKeys.F16));
			io.AddKeyEvent(ImGuiKey.F17, Input.Keyboard.GetKey(KeyboardKeys.F17));
			io.AddKeyEvent(ImGuiKey.F18, Input.Keyboard.GetKey(KeyboardKeys.F18));
			io.AddKeyEvent(ImGuiKey.F19, Input.Keyboard.GetKey(KeyboardKeys.F19));
			io.AddKeyEvent(ImGuiKey.F20, Input.Keyboard.GetKey(KeyboardKeys.F20));
            io.AddKeyEvent(ImGuiKey.F21, Input.Keyboard.GetKey(KeyboardKeys.F21));
			io.AddKeyEvent(ImGuiKey.F22, Input.Keyboard.GetKey(KeyboardKeys.F22));
			io.AddKeyEvent(ImGuiKey.F23, Input.Keyboard.GetKey(KeyboardKeys.F23));
			io.AddKeyEvent(ImGuiKey.F24, Input.Keyboard.GetKey(KeyboardKeys.F24));
			io.AddKeyEvent(ImGuiKey.Apostrophe, Input.Keyboard.GetKey(KeyboardKeys.BackQuote));
			io.AddKeyEvent(ImGuiKey.Comma, Input.Keyboard.GetKey(KeyboardKeys.Comma));
			io.AddKeyEvent(ImGuiKey.Minus, Input.Keyboard.GetKey(KeyboardKeys.Minus));
			io.AddKeyEvent(ImGuiKey.Period, Input.Keyboard.GetKey(KeyboardKeys.Period));
			io.AddKeyEvent(ImGuiKey.Slash, Input.Keyboard.GetKey(KeyboardKeys.Slash));
			io.AddKeyEvent(ImGuiKey.Semicolon, Input.Keyboard.GetKey(KeyboardKeys.Colon));
			io.AddKeyEvent(ImGuiKey.LeftBracket, Input.Keyboard.GetKey(KeyboardKeys.LeftBracket));
			io.AddKeyEvent(ImGuiKey.Backslash, Input.Keyboard.GetKey(KeyboardKeys.Backslash));
			io.AddKeyEvent(ImGuiKey.RightBracket, Input.Keyboard.GetKey(KeyboardKeys.RightBracket));
			io.AddKeyEvent(ImGuiKey.CapsLock, Input.Keyboard.GetKey(KeyboardKeys.Capital));
			io.AddKeyEvent(ImGuiKey.ScrollLock, Input.Keyboard.GetKey(KeyboardKeys.Scroll));
			io.AddKeyEvent(ImGuiKey.NumLock, Input.Keyboard.GetKey(KeyboardKeys.Numlock));
			io.AddKeyEvent(ImGuiKey.PrintScreen, Input.Keyboard.GetKey(KeyboardKeys.PrintScreen));
			io.AddKeyEvent(ImGuiKey.Pause, Input.Keyboard.GetKey(KeyboardKeys.Pause));
			io.AddKeyEvent(ImGuiKey.Keypad0, Input.Keyboard.GetKey(KeyboardKeys.Numpad0));
			io.AddKeyEvent(ImGuiKey.Keypad1, Input.Keyboard.GetKey(KeyboardKeys.Numpad1));
			io.AddKeyEvent(ImGuiKey.Keypad2, Input.Keyboard.GetKey(KeyboardKeys.Numpad2));
			io.AddKeyEvent(ImGuiKey.Keypad3, Input.Keyboard.GetKey(KeyboardKeys.Numpad3));
			io.AddKeyEvent(ImGuiKey.Keypad4, Input.Keyboard.GetKey(KeyboardKeys.Numpad4));
			io.AddKeyEvent(ImGuiKey.Keypad5, Input.Keyboard.GetKey(KeyboardKeys.Numpad5));
			io.AddKeyEvent(ImGuiKey.Keypad6, Input.Keyboard.GetKey(KeyboardKeys.Numpad6));
			io.AddKeyEvent(ImGuiKey.Keypad7, Input.Keyboard.GetKey(KeyboardKeys.Numpad7));
			io.AddKeyEvent(ImGuiKey.Keypad8, Input.Keyboard.GetKey(KeyboardKeys.Numpad8));
			io.AddKeyEvent(ImGuiKey.Keypad9, Input.Keyboard.GetKey(KeyboardKeys.Numpad9));
			io.AddKeyEvent(ImGuiKey.KeypadDecimal, Input.Keyboard.GetKey(KeyboardKeys.NumpadDecimal));
			io.AddKeyEvent(ImGuiKey.KeypadDivide, Input.Keyboard.GetKey(KeyboardKeys.NumpadDivide));
			io.AddKeyEvent(ImGuiKey.KeypadMultiply, Input.Keyboard.GetKey(KeyboardKeys.NumpadMultiply));
			io.AddKeyEvent(ImGuiKey.KeypadSubtract, Input.Keyboard.GetKey(KeyboardKeys.NumpadSubtract));
			io.AddKeyEvent(ImGuiKey.KeypadAdd, Input.Keyboard.GetKey(KeyboardKeys.NumpadAdd));
			io.AddKeyEvent(ImGuiKey.AppBack, Input.Keyboard.GetKey(KeyboardKeys.BrowserBack));
			io.AddKeyEvent(ImGuiKey.AppForward, Input.Keyboard.GetKey(KeyboardKeys.BrowserFavorites));
			io.AddKeyEvent(ImGuiKey.Oem102, Input.Keyboard.GetKey(KeyboardKeys.Oem102));
			io.AddKeyEvent(ImGuiKey.ModCtrl, Input.Keyboard.GetKey(KeyboardKeys.Control));
			io.AddKeyEvent(ImGuiKey.ModShift, Input.Keyboard.GetKey(KeyboardKeys.Shift));
			io.AddKeyEvent(ImGuiKey.ModAlt, Input.Keyboard.GetKey(KeyboardKeys.Alt));
			io.AddKeyEvent(ImGuiKey.ModSuper, Input.Keyboard.GetKey(KeyboardKeys.LeftWindows) ||
			                                  Input.Keyboard.GetKey(KeyboardKeys.RightWindows));

			// Gamepad mappings
			if ((io.BackendFlags & ImGuiBackendFlags.HasGamepad) == 0 && Input.GamepadsCount == 0)
				return;
			
			io.AddKeyEvent(ImGuiKey.GamepadStart, Input.GetGamepadButton(0, GamepadButton.Start));
			io.AddKeyEvent(ImGuiKey.GamepadBack, Input.GetGamepadButton(0, GamepadButton.Back));
			io.AddKeyEvent(ImGuiKey.GamepadFaceLeft, Input.GetGamepadButton(0, GamepadButton.X));
			io.AddKeyEvent(ImGuiKey.GamepadFaceRight, Input.GetGamepadButton(0, GamepadButton.B));
			io.AddKeyEvent(ImGuiKey.GamepadFaceUp, Input.GetGamepadButton(0, GamepadButton.Y));
			io.AddKeyEvent(ImGuiKey.GamepadFaceDown, Input.GetGamepadButton(0, GamepadButton.A));
			io.AddKeyEvent(ImGuiKey.GamepadDpadLeft, Input.GetGamepadButton(0, GamepadButton.DPadLeft));
			io.AddKeyEvent(ImGuiKey.GamepadDpadRight, Input.GetGamepadButton(0, GamepadButton.DPadRight));
			io.AddKeyEvent(ImGuiKey.GamepadDpadUp, Input.GetGamepadButton(0, GamepadButton.DPadUp));
			io.AddKeyEvent(ImGuiKey.GamepadDpadDown, Input.GetGamepadButton(0, GamepadButton.DPadDown));
			io.AddKeyEvent(ImGuiKey.GamepadL1, Input.GetGamepadButton(0, GamepadButton.LeftShoulder));
			io.AddKeyEvent(ImGuiKey.GamepadR1, Input.GetGamepadButton(0, GamepadButton.RightShoulder));
			io.AddKeyEvent(ImGuiKey.GamepadL3, Input.GetGamepadButton(0, GamepadButton.LeftThumb));
			io.AddKeyEvent(ImGuiKey.GamepadR3, Input.GetGamepadButton(0, GamepadButton.RightThumb));
			
			const float deadZone = 0.2f;
			float leftStickX = Input.GetGamepadAxis(0, GamepadAxis.LeftStickX);
			float leftStickY = Input.GetGamepadAxis(0, GamepadAxis.LeftStickY);
			float rightStickX = Input.GetGamepadAxis(0, GamepadAxis.RightStickX);
			float rightStickY = Input.GetGamepadAxis(0, GamepadAxis.RightStickY);
			float triggerLeft = Input.GetGamepadAxis(0, GamepadAxis.LeftTrigger);
			float triggerRight = Input.GetGamepadAxis(0, GamepadAxis.RightTrigger);
			
			io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickRight, leftStickX > deadZone, leftStickX);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickLeft, leftStickX < -deadZone, -leftStickX);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickDown, leftStickY > deadZone, leftStickY);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickUp, leftStickY < -deadZone, -leftStickY);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickRight, rightStickX > deadZone, rightStickX);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickLeft, rightStickX < -deadZone, -rightStickX);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickDown, rightStickY > deadZone, rightStickY);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickUp, rightStickY < -deadZone, -rightStickY);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadL2, triggerLeft > deadZone * 0.5f, triggerLeft);
			io.AddKeyAnalogEvent(ImGuiKey.GamepadR2, triggerRight > deadZone * 0.5f, triggerRight);
		}
	}

	private void OnLateUpdate()
	{
		if (!_activeFrame)
			return;
		
		// End frame
		if (ImGuiP.GetCurrentWindowRead() == ImGuiWindowPtr.Null)
			return;
		
#if INCLUDE_IMWIDGETS
		Hexa.NET.ImGui.Widgets.WidgetManager.Draw();
#endif
		ImGui.Render();
		_activeFrame = false;
	}
	
	private unsafe void OnPostRender(GPUContext arg0, ref RenderContext context)
	{
		// Draw ImGui data into the output (via Render2D)
		var drawData = ImGui.GetDrawData();
		if (drawData.IsNull)
			return;
		
		// Update textures
		for (int i = 0; i < drawData.Textures.Size; i++)
			if (!drawData.Textures[i].IsNull && drawData.Textures[i].Status != ImTextureStatus.Ok)
				UpdateTexture(arg0, drawData.Textures[i]);

		// Render command lists
		var viewport = context.Task.OutputViewport;
		Render2D.Begin(arg0, context.Task.OutputView, null, ref viewport);
		
		for (int cmdListIndex = 0; cmdListIndex < drawData.CmdListsCount; cmdListIndex++)
		{
			var cmdList = drawData.CmdLists[cmdListIndex];

			// Resize internal arrays to fit buffers
			if (cmdList.VtxBuffer.Size > _vertices.Length)
			{
				int newSize = NextPowerOf2(cmdList.VtxBuffer.Size);
				_vertices = new Float2[newSize];
				_uvs = new Float2[newSize];
				_colors = new Color[newSize];
			}
			if (cmdList.IdxBuffer.Size > _indices.Length)
			{
				_indices = new ushort[NextPowerOf2(cmdList.IdxBuffer.Size)];
			}
			
			// Convert vertex buffer
			for (int i = 0; i < cmdList.VtxBuffer.Size; i++)
			{
				var v = cmdList.VtxBuffer.Data[i];
				// Offset vertices for pixel alignment
				_vertices[i] = new Float2(v.Pos.X + 0.5f, v.Pos.Y + 0.5f); 
				_uvs[i] = new Float2(v.Uv.X, v.Uv.Y);
				_colors[i] = new Color(
					(byte)(v.Col & 0xFF),
					(byte)((v.Col >> 8) & 0xFF),
					(byte)((v.Col >> 16) & 0xFF),
					(byte)((v.Col >> 24) & 0xFF));
			}
			
			// Convert index buffer
			for (int i = 0; i < cmdList.IdxBuffer.Size; i++)
			{
				_indices[i] = cmdList.IdxBuffer.Data[i];
			}
			
			// Submit draw commands
			for (int cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
			{
				var cmd = cmdList.CmdBuffer[cmdIndex];
				if (cmd.UserCallback != null)
				{
					delegate*<ImDrawList*, ImDrawCmd*, void> userCallback = 
						(delegate*<ImDrawList*, ImDrawCmd*, void>)cmd.UserCallback;

					userCallback(cmdList, &cmd);
				}
				else
				{
					// Perform scissors clipping
					Float2 clipMin = new Float2(
						(cmd.ClipRect.X - drawData.DisplayPos.X) * drawData.FramebufferScale.X, 
						(cmd.ClipRect.Y - drawData.DisplayPos.Y) * drawData.FramebufferScale.Y);
					Float2 clipMax = new Float2(
						(cmd.ClipRect.Z - drawData.DisplayPos.X) * drawData.FramebufferScale.X, 
						(cmd.ClipRect.W - drawData.DisplayPos.Y) * drawData.FramebufferScale.Y);
					if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y)
						continue;
					
					Rectangle scissor = new Rectangle(
						clipMin.X, clipMin.Y, clipMax.X - clipMin.X, clipMax.Y - clipMin.Y);
					Render2D.PushClip(scissor);
					
					// Draw textured indexed triangles list
					int texId = (int)cmd.GetTexID().Handle - 1;
					var tex = _textures[texId];
					
					Render2D.DrawTexturedTriangles(tex, 
						new Span<ushort>(_indices, (int)cmd.IdxOffset, (int)cmd.ElemCount).ToArray(), 
						new Span<Float2>(_vertices, (int)cmd.VtxOffset, _vertices.Length - (int)cmd.VtxOffset).ToArray(), 
						new Span<Float2>(_uvs, (int)cmd.VtxOffset, _uvs.Length - (int)cmd.VtxOffset).ToArray(), 
						new Span<Color>(_colors, (int)cmd.VtxOffset, _colors.Length - (int)cmd.VtxOffset).ToArray());
					Render2D.PopClip();
				}
			}
		}
		
		Render2D.End();
		ImGui.EndFrame();
	}
	
	/// <inheritdoc />
	public override void Deinitialize()
	{
		LibraryLoader.InterceptLibraryLoad -= InterceptLibraryLoad;
		
		// Unbind update events
		Scripting.Update -= OnUpdate;
		Scripting.LateUpdate -= OnLateUpdate;
		MainRenderTask.Instance.PostRender -= OnPostRender;
		
		// Shutdown
#if INCLUDE_IMNODES 
		ImNodes.DestroyContext(_imNodesContext);
#endif
#if INCLUDE_IMPLOT 
		ImPlot.DestroyContext(_imPlotContext);
#endif
#if INCLUDE_IMWIDGETS
		Hexa.NET.ImGui.Widgets.WidgetManager.Dispose();
#endif
		ImGui.DestroyContext(_imGuiContext);
		
		base.Deinitialize();
	}

	/// <summary>
	/// Handles loading of each native library per Hexa.NET.ImGui module.
	/// </summary>
	/// <param name="name">The name of the library.</param>
	/// <param name="pointer">The native pointer to the library.</param>
	/// <returns></returns>
	private static bool InterceptLibraryLoad(string name, out IntPtr pointer)
	{
		pointer = IntPtr.Zero;
		string path = "";
		if (Engine.IsEditor)
		{
			// Editor path
			path = Path.Combine(Globals.ProjectFolder, "Plugins", "FlexaImGui", "Content", "runtimes");
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
					path = Path.Combine(path, "linux-arm64", "native", $"{name}.so");
				else
					path = Path.Combine(path, "linux-x64", "native", $"{name}.so");
			}
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				switch (RuntimeInformation.OSArchitecture)
				{
					case Architecture.Arm64:
						path = Path.Combine(path, "win-arm64", "native", $"{name}.dll");
						break;
					case Architecture.X86:
						path = Path.Combine(path, "win-x86", "native", $"{name}.dll");
						break;
					default:
						path = Path.Combine(path, "win-x64", "native", $"{name}.dll");
						break;
				}
			}
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			{
				if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
					path = Path.Combine(path, "osx-arm64", "native", $"{name}.dylib");
				else
					path = Path.Combine(path, "osx-x64", "native", $"{name}.dylib");
			}
		}

		if (NativeLibrary.TryLoad(path, out IntPtr nativePointer))
		{
			pointer = nativePointer;
			return true;
		}

		return false;
	}
	
	/// <summary>
	/// Dynamic texture update method.
	/// </summary>
	/// <param name="arg0">The passed GPUContext.</param>
	/// <param name="tex">The passed ImTextureData pointer for the dynamic texture.</param>
	private unsafe void UpdateTexture(GPUContext arg0, ImTextureData* tex)
	{
		if (tex->Status == ImTextureStatus.WantCreate)
		{
			var texture = GPUDevice.Instance.CreateTexture((_textures.Length + 1).ToString());
			var desc = GPUTextureDescription.New2D(tex->Width, tex->Height, 1,
				PixelFormat.B8G8R8A8_UNorm, GPUTextureFlags.ShaderResource);

			if (texture.Init(ref desc))
			{
				Debug.LogError("Failed to setup ImGui font atlas texture.");
				return;
			}
			
			var size = desc.Width * desc.Height * PixelFormatExtensions.SizeInBytes(desc.Format);
			byte[] data = new byte[size];
			
			for(int i = 0; i < size; i++)
				data[i] = tex->Pixels[i];
			
			fixed (byte* dataPtr = data)
			{
				uint rowPitch = (uint)size / (uint)desc.Height;
				uint slicePitch = (uint)size;
				
				arg0.UpdateTexture(texture, 0, 0, new IntPtr(dataPtr), rowPitch, slicePitch);
				texture.ResidentMipLevels = 1;
			}
			
			tex->SetTexID(new ImTextureID(_freeOffset + 1));
			
			if (_freeOffset > _textures.Length - 1)
				Array.Resize(ref _textures, _textures.Length * 2);
			
			_textures[_freeOffset] = texture;
			tex->SetStatus(ImTextureStatus.Ok);
			RefreshTextureArray();
		}
		if (tex->Status == ImTextureStatus.WantUpdates)
		{
			var updateRect = tex->UpdateRect;
			var texture = _textures[tex->GetTexID() - 1];
			
			var desc = texture.Description;
			var size = desc.Width * desc.Height * PixelFormatExtensions.SizeInBytes(desc.Format);
			byte[] data = new byte[size];
			
			for(int i = 0; i < size; i++)
				data[i] = tex->Pixels[i];
			
			fixed (byte* dataPtr = data)
			{
				uint rowPitch = (uint)size / (uint)desc.Height;
				uint slicePitch = (uint)size;
				
				arg0.UpdateTexture(texture, 0, 0, new IntPtr(dataPtr), rowPitch, slicePitch);
				texture.ResidentMipLevels = 1;
			}
			tex->SetStatus(ImTextureStatus.Ok);
		}
		if (tex->Status == ImTextureStatus.WantDestroy && tex->UnusedFrames > 0)
		{
			int id = (int)tex->TexID.Handle - 1;
			_textures[id].ReleaseGPU();
			tex->SetTexID(ImTextureID.Null);
			tex->SetStatus(ImTextureStatus.Destroyed);
			RefreshTextureArray();
		}
	}

	/// <summary>
	/// Refreshes the internal texture array.
	/// </summary>
	private void RefreshTextureArray()
	{
		_endOffset = _textures.Length;
		for (int i = 0; i < _endOffset; i++)
		{
			if (_textures[i] == null || !_textures[i].IsAllocated)
			{
				_freeOffset = i;
				return;
			}
		}
		_freeOffset = _endOffset;
	}
	
	/// <summary>
	/// Calls NewFrame() for each Hexa.NET.ImGui module.
	/// </summary>
	public static void NewFrame()
	{
		ImGui.NewFrame();
#if INCLUDE_IMGUIZMO
		ImGuizmo.BeginFrame();
#endif
	}

	/// <summary>
	/// Registers a texture for the ImGui context.
	/// </summary>
	/// <param name="texture">The texture to register.</param>
	/// <returns>The ImGui ID handle for the texture.</returns>
	public int RegisterTexture(GPUTexture texture)
	{
		if (_freeOffset > _textures.Length - 1)
			Array.Resize(ref _textures, _textures.Length * 2);
			
		_textures[_freeOffset] = texture;
		int texIndex = _freeOffset + 1;
		RefreshTextureArray();
		return texIndex;
	}

	/// <summary>
	/// Releases a texture previously registered for the ImGui context. Meant for handling custom registered textures via <see cref="RegisterTexture"/>, as
	/// other textures (e.g. dynamic textures for fonts) are handled internally.
	/// </summary>
	/// <param name="id">The ImGui ID handle for the texture to release.</param>
	public void ReleaseTexture(int id)
	{
		_textures[id - 1].ReleaseGPU();
		_textures[id - 1] = null;
		RefreshTextureArray();
	}
	
	/// <summary>
	/// Loads a .ttf font into ImGui by name (extension excluded).
	/// </summary>
	/// <param name="name">Name of the .ttf font inside '/Plugins/FlexaImGui/Content/'.</param>
	/// <param name="font">ImFont pointer to the loaded font.</param>
	/// <param name="size">The size of the loaded font (height in pixels).</param>
	/// <returns>True if the font could be loaded.</returns>
	public static unsafe bool AddFontFromFileTTF(string name, out ImFont* font, float size = 13)
	{
		string fontPath = Engine.IsEditor
			? Path.Combine(Globals.ProjectFolder, "Plugins", "FlexaImGui", "Content", $"{name}.ttf")
			: Path.Combine(Globals.ProjectFolder, $"{name}.ttf");

		if (File.Exists(fontPath))
		{
			font = ImGui.GetIO().Fonts.AddFontFromFileTTF(fontPath, size);
			return font != ImFontPtr.Null;
		}

		Debug.LogWarning($"[FlexaImGui] Font file '{name}' could not be found at {fontPath}!");
		font = null;
		return false;
	}
	
	/// <returns>The nearest power of 2 that can fit <c>x</c> (greater than or equal).</returns>
	public static int NextPowerOf2(int x)
	{
		if (x <= 0) return 1; x--;
		x |= x >> 1;
		x |= x >> 2;
		x |= x >> 4;
		x |= x >> 8;
		x |= x >> 16;
		return x + 1;
	}
}