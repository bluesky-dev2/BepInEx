﻿using System;
using System.Reflection;
using UnityEngine;

namespace BepInEx.Logging
{
	/// <summary>
	/// Logs entries using Unity specific outputs.
	/// </summary>
	public class UnityLogSource : ILogSource
	{
		public string SourceName { get; } = "Unity Log";
		public event EventHandler<LogEventArgs> LogEvent;

		public UnityLogSource()
		{
			InternalUnityLogMessage += unityLogMessageHandler;
		}

		private void unityLogMessageHandler(object sender, LogEventArgs eventArgs)
		{
			var newEventArgs = new LogEventArgs(eventArgs.Data, eventArgs.Level, this);
			LogEvent?.Invoke(this, newEventArgs);
		}

		private bool disposed = false;

		public void Dispose()
		{
			if (!disposed)
			{
				InternalUnityLogMessage -= unityLogMessageHandler;
				disposed = true;
			}
		}

		#region Static Unity handler

		private static event EventHandler<LogEventArgs> InternalUnityLogMessage;

		static UnityLogSource()
		{
			var callback = new Application.LogCallback(OnUnityLogMessageReceived);

			EventInfo logEvent = typeof(Application).GetEvent("logMessageReceived", BindingFlags.Public | BindingFlags.Static);
			if (logEvent != null)
			{
				logEvent.AddEventHandler(null, callback);
				//UnsubscribeAction = () => logEvent.RemoveEventHandler(null, callback);
			}
			else
			{
				MethodInfo registerLogCallback = typeof(Application).GetMethod("RegisterLogCallback", BindingFlags.Public | BindingFlags.Static);
				registerLogCallback.Invoke(null, new object[] { callback });
				//UnsubscribeAction = () => registerLogCallback.Invoke(null, new object[] { null });
			}
		}

		private static void OnUnityLogMessageReceived(string message, string stackTrace, LogType type)
		{
			LogLevel logLevel;

			switch (type)
			{
				case LogType.Error:
				case LogType.Assert:
				case LogType.Exception:
					logLevel = LogLevel.Error;
					break;
				case LogType.Warning:
					logLevel = LogLevel.Warning;
					break;
				case LogType.Log:
				default:
					logLevel = LogLevel.Info;
					break;
			}

			if (type == LogType.Exception)
				message += $"\nStack trace:\n{stackTrace}";

			InternalUnityLogMessage?.Invoke(null, new LogEventArgs(message, logLevel, null));
		}

		#endregion
	}
}