﻿using System;
using BepInEx.Configuration;

namespace BepInEx.Logging
{
	/// <summary>
	/// Logs entries using Unity specific outputs.
	/// </summary>
	public class ConsoleLogListener : ILogListener
	{
		/// <inheritdoc />
		public void LogEvent(object sender, LogEventArgs eventArgs)
		{
			if ((eventArgs.Level & ConfigConsoleDisplayedLevel.Value) == 0)
				return;
			
			ConsoleManager.SetConsoleColor(eventArgs.Level.GetConsoleColor());
			ConsoleManager.ConsoleStream?.Write(eventArgs.ToStringLine());
			ConsoleManager.SetConsoleColor(ConsoleColor.Gray);
		}

		/// <inheritdoc />
		public void Dispose() { }

		private static readonly ConfigEntry<LogLevel> ConfigConsoleDisplayedLevel = ConfigFile.CoreConfig.Bind(
			"Logging.Console","LogLevels",
			LogLevel.Fatal | LogLevel.Error | LogLevel.Message | LogLevel.Info | LogLevel.Warning,
			"Which log levels to show in the console output.");
	}
}