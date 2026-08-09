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
				var path = Environment.GetFolderPath(Environment.SpecialFolder.Personal)
					+ @"\Asheron's Call\" + Globals.PluginName + " trace.txt";

				using (var w = new StreamWriter(path, true))
				{
					w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
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
