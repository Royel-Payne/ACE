using System;
using System.IO;

namespace ShadowgainConsole
{
	public static class Util
	{
		public static void LogError(Exception ex)
		{
			try
			{
				using (StreamWriter writer = new StreamWriter(Environment.GetFolderPath(Environment.SpecialFolder.Personal) + @"\Asheron's Call\" + Globals.PluginName + " errors.txt", true))
				{
					writer.WriteLine("============================================================================");
					writer.WriteLine(DateTime.Now.ToString());
					writer.WriteLine("Error: " + ex.Message);
					writer.WriteLine("Source: " + ex.Source);
					writer.WriteLine("Stack: " + ex.StackTrace);
					if (ex.InnerException != null)
					{
						writer.WriteLine("Inner: " + ex.InnerException.Message);
						writer.WriteLine("Inner Stack: " + ex.InnerException.StackTrace);
					}
					writer.WriteLine("============================================================================");
					writer.WriteLine("");
					writer.Close();
				}
			}
			catch
			{
			}
		}

		/// <summary>
		/// Append one breadcrumb, opened and closed per call so it is on disk before the next
		/// line runs.
		///
		/// This exists because the client was closing on world entry with an access violation
		/// INSIDE acclient.exe - a native crash, so no managed exception was ever thrown, no
		/// catch block fired, and the error log stayed empty. There was nothing to read. A
		/// buffered writer would have been just as useless: anything still in the buffer dies
		/// with the process. Whatever is in this file is what completed; the first missing line
		/// is where it died.
		/// </summary>
		public static void Trace(string message)
		{
			try
			{
				// A CONSTANT, not Globals.PluginName. The name is only set by Globals.Init, and
				// the first trace call deliberately runs before it - so this used to build a path
				// with an empty segment and quietly create a file literally called " trace.txt"
				// in the user's Documents. The one line that mattered most, the very first, went
				// somewhere nobody would look.
				var path = Environment.GetFolderPath(Environment.SpecialFolder.Personal)
					+ @"\Asheron's Call\ShadowgainConsole trace.txt";

				// Roll at a quarter of a megabyte. The console fires a command a minute while its
				// window is open, so an unattended client would grow this forever. One previous
				// generation is kept, because the interesting part of a crash is usually the
				// lines just BEFORE the roll.
				try
				{
					var info = new FileInfo(path);

					if (info.Exists && info.Length > 256 * 1024)
					{
						var previous = path + ".prev";

						if (File.Exists(previous))
							File.Delete(previous);

						File.Move(path, previous);
					}
				}
				catch
				{
					// A roll that fails must not cost us the log line itself.
				}

				using (var w = new StreamWriter(path, true))
				{
					w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message);
					w.Flush();
				}
			}
			catch
			{
			}
		}

		public static void WriteToChat(string message)
		{
			try
			{
				Globals.Host.Actions.AddChatText("<{" + Globals.PluginName + "}>: " + message, 5);
			}
			catch (Exception ex) { LogError(ex); }
		}
	}
}
