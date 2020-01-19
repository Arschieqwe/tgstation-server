﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using Tgstation.Server.Host.Extensions;

namespace Tgstation.Server.Host.Core.Tests
{
	[TestClass]
	public sealed class TestServiceCollectionExtensions
	{
		class GoodConfig
		{
			public const string Section = "asdf";
		}

		class BadConfig1
		{
			const string Section = "asdf";
		}

		class BadConfig2
		{
			public const bool Section = false;
		}

		class BadConfig3
		{
			//nah
		}

		[TestMethod]
		public void TestUseStandardConfig()
		{
			var serviceCollection = new ServiceCollection();
			var mockConfig = new Mock<IConfigurationSection>();
			mockConfig.Setup(x => x.GetSection(It.IsNotNull<string>())).Returns(mockConfig.Object).Verifiable();
			Assert.ThrowsException<ArgumentNullException>(() => ServiceCollectionExtensions.UseStandardConfig<GoodConfig>(null, null));
			Assert.ThrowsException<ArgumentNullException>(() => serviceCollection.UseStandardConfig<GoodConfig>(null));
			serviceCollection.UseStandardConfig<GoodConfig>(mockConfig.Object);
			Assert.ThrowsException<InvalidOperationException>(() => serviceCollection.UseStandardConfig<BadConfig1>(mockConfig.Object));
			Assert.ThrowsException<InvalidOperationException>(() => serviceCollection.UseStandardConfig<BadConfig2>(mockConfig.Object));
			Assert.ThrowsException<InvalidOperationException>(() => serviceCollection.UseStandardConfig<BadConfig3>(mockConfig.Object));
			mockConfig.Verify();
		}
	}
}
