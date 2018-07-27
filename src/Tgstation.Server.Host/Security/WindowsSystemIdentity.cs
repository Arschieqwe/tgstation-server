﻿using System;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Tgstation.Server.Host.Security
{
	/// <summary>
	/// <see cref="ISystemIdentity"/> for windows systems
	/// </summary>
	sealed class WindowsSystemIdentity : ISystemIdentity
	{
		/// <summary>
		/// The <see cref="WindowsIdentity"/> for the <see cref="WindowsSystemIdentity"/>
		/// </summary>
		readonly WindowsIdentity identity;

		/// <summary>
		/// The <see cref="UserPrincipal"/> for the <see cref="WindowsSystemIdentity"/>
		/// </summary>
		readonly UserPrincipal userPrincipal;

		/// <summary>
		/// Construct a <see cref="WindowsSystemIdentity"/> using a <see cref="WindowsIdentity"/>
		/// </summary>
		/// <param name="identity">The value of <see cref="identity"/></param>
		public WindowsSystemIdentity(WindowsIdentity identity)
		{
			this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
		}

		/// <summary>
		/// Construct a <see cref="WindowsSystemIdentity"/> using a <see cref="UserPrincipal"/>
		/// </summary>
		/// <param name="userPrincipal">The value of <see cref="userPrincipal"/></param>
		public WindowsSystemIdentity(UserPrincipal userPrincipal)
		{
			this.userPrincipal = userPrincipal ?? throw new ArgumentNullException(nameof(userPrincipal));
		}

		/// <inheritdoc />
		public void Dispose()
		{
			if (identity != null)
				identity.Dispose();
			else
				userPrincipal.Dispose();
		}

		/// <inheritdoc />
		public string Uid => (userPrincipal?.Sid ?? identity.User).ToString();

		/// <inheritdoc />
		public string Username => userPrincipal?.Name ?? identity.Name;

		/// <inheritdoc />
		public ISystemIdentity Clone()
		{
			if (identity != null)
				return new WindowsSystemIdentity((WindowsIdentity)identity.Clone());
			//can't clone a UP, shouldn't be trying to anyway, cloning is for impersonation
			throw new InvalidOperationException("Cannot clone a UserPrincipal based WindowsSystemIdentity!");
		}

		/// <inheritdoc />
		public Task RunImpersonated(Action action, CancellationToken cancellationToken) => Task.Factory.StartNew(() =>
		{
			if (action == null)
				throw new ArgumentNullException(nameof(action));
			if (identity == null)
				throw new InvalidOperationException("Impersonate using a UserPrincipal based WindowsSystemIdentity!");
			WindowsIdentity.RunImpersonated(identity.AccessToken, action);
		}, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Current);
	}
}