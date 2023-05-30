﻿using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tgstation.Server.Tests.Live
{
	sealed class HardFailLogger : ILogger
	{
		readonly Action<Exception> failureSink;

		public HardFailLogger(Action<Exception> failureSink)
		{
			this.failureSink = failureSink ?? throw new ArgumentNullException(nameof(failureSink));
		}

		public IDisposable BeginScope<TState>(TState state) => Task.CompletedTask;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
		{
			var logMessage = formatter(state, exception);
			if ((logLevel == LogLevel.Error
				&& !logMessage.StartsWith("Error disconnecting connection ")
				&& !(logMessage.StartsWith("An exception occurred while iterating over the results of a query for context type") && (exception is TaskCanceledException || exception?.InnerException is TaskCanceledException)))
				|| (logLevel == LogLevel.Critical && logMessage != "DropDatabase configuration option set! Dropping any existing database..."))
			{
				Console.WriteLine("UNEXPECTED ERROR OCCURS NEAR HERE");
				failureSink(new AssertFailedException("TGS logged an unexpected error!"));
			}
		}
	}
}