using System;
namespace MonoDevelop.FSharp.Gui
{
	[System.ComponentModel.ToolboxItem(true)]
	public partial class FSharpSettingsWidget : Gtk.Bin
	{
		public FSharpSettingsWidget ()
		{
			this.Build ();
		}
		
		public Gtk.CheckButton CheckInteractiveUseDefault { get { return checkInteractiveUseDefault; } }
    	public Gtk.Button ButtonBrowse { get { return buttonBrowse; } }
    	public Gtk.Entry EntryArguments { get { return entryArguments; } }
	    public Gtk.Entry EntryPath { get { return entryPath; } }
	    public Gtk.FontButton FontInteractive { get { return fontbutton1; } }
		public Gtk.CheckButton CheckCompilerUseDefault { get { return checkCompilerUseDefault; } }
		public Gtk.CheckButton EnableFSharp30 { get { return this.enableFSharp3; } }
		public Gtk.Button ButtonCompilerBrowse { get { return buttonCompilerBrowse; } }
		public Gtk.Entry EntryCompilerPath { get { return entryCompilerPath; } }
		
	}
}

