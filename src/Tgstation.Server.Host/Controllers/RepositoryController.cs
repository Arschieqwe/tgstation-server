﻿using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Tgstation.Server.Api;
using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Request;
using Tgstation.Server.Api.Models.Response;
using Tgstation.Server.Api.Rights;
using Tgstation.Server.Host.Components;
using Tgstation.Server.Host.Components.Repository;
using Tgstation.Server.Host.Database;
using Tgstation.Server.Host.Extensions;
using Tgstation.Server.Host.Jobs;
using Tgstation.Server.Host.Models;
using Tgstation.Server.Host.Security;

namespace Tgstation.Server.Host.Controllers
{
	/// <summary>
	/// <see cref="ApiController"/> for managing the git repository.
	/// </summary>
	[Route(Routes.Repository)]
#pragma warning disable CA1506 // TODO: Decomplexify
	public sealed class RepositoryController : InstanceRequiredController
	{
		/// <summary>
		/// The <see cref="ILoggerFactory"/> for the <see cref="RepositoryController"/>.
		/// </summary>
		readonly ILoggerFactory loggerFactory;

		/// <summary>
		/// The <see cref="IJobManager"/> for the <see cref="RepositoryController"/>.
		/// </summary>
		readonly IJobManager jobManager;

		/// <summary>
		/// Initializes a new instance of the <see cref="RepositoryController"/> class.
		/// </summary>
		/// <param name="databaseContext">The <see cref="IDatabaseContext"/> for the <see cref="InstanceRequiredController"/>.</param>
		/// <param name="authenticationContextFactory">The <see cref="IAuthenticationContextFactory"/> for the <see cref="InstanceRequiredController"/>.</param>
		/// <param name="logger">The <see cref="ILogger"/> for the <see cref="InstanceRequiredController"/>.</param>
		/// <param name="instanceManager">The <see cref="IInstanceManager"/> for the <see cref="InstanceRequiredController"/>.</param>
		/// <param name="loggerFactory">The value of <see cref="loggerFactory"/>.</param>
		/// <param name="jobManager">The value of <see cref="jobManager"/>.</param>
		public RepositoryController(
			IDatabaseContext databaseContext,
			IAuthenticationContextFactory authenticationContextFactory,
			ILogger<RepositoryController> logger,
			IInstanceManager instanceManager,
			ILoggerFactory loggerFactory,
			IJobManager jobManager)
			: base(
				  databaseContext,
				  authenticationContextFactory,
				  logger,
				  instanceManager)
		{
			this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
			this.jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
		}

		/// <summary>
		/// Begin cloning the repository if it doesn't exist.
		/// </summary>
		/// <param name="model">The <see cref="RepositoryCreateRequest"/>.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the <see cref="IActionResult"/> of the request.</returns>
		/// <response code="201">The repository was created successfully and the <see cref="JobResponse"/> to clone it has begun.</response>
		/// <response code="410">The database entity for the requested instance could not be retrieved. The instance was likely detached.</response>
		[HttpPut]
		[TgsAuthorize(RepositoryRights.SetOrigin)]
		[ProducesResponseType(typeof(RepositoryResponse), 201)]
		[ProducesResponseType(typeof(ErrorMessageResponse), 410)]
		public async Task<IActionResult> Create([FromBody] RepositoryCreateRequest model, CancellationToken cancellationToken)
		{
			if (model == null)
				throw new ArgumentNullException(nameof(model));

			if (model.Origin == null)
				return BadRequest(ErrorCode.ModelValidationFailure);

			if (model.AccessUser == null ^ model.AccessToken == null)
				return BadRequest(ErrorCode.RepoMismatchUserAndAccessToken);

			var userRights = (RepositoryRights)AuthenticationContext.GetRight(RightsType.Repository);
			if (((model.AccessUser ?? model.AccessToken) != null && !userRights.HasFlag(RepositoryRights.ChangeCredentials))
				|| ((model.CommitterEmail ?? model.CommitterName) != null && !userRights.HasFlag(RepositoryRights.ChangeCommitter)))
				return Forbid();

			#pragma warning disable CS0618 // Support for obsolete API field
			model.UpdateSubmodules ??= model.RecurseSubmodules;
			#pragma warning restore CS0618

			var currentModel = await DatabaseContext
				.RepositorySettings
				.AsQueryable()
				.Where(x => x.InstanceId == Instance.Id)
				.FirstOrDefaultAsync(cancellationToken);

			if (currentModel == default)
				return this.Gone();

			currentModel.UpdateSubmodules = model.UpdateSubmodules ?? true;
			currentModel.AccessToken = model.AccessToken;
			currentModel.AccessUser = model.AccessUser;

			currentModel.CommitterEmail = model.CommitterEmail ?? currentModel.CommitterEmail;
			currentModel.CommitterName = model.CommitterName ?? currentModel.CommitterName;

			var cloneBranch = model.Reference;
			var origin = model.Origin;

			return await WithComponentInstance(
				async instance =>
				{
					var repoManager = instance.RepositoryManager;

					if (repoManager.CloneInProgress)
						return Conflict(new ErrorMessageResponse(ErrorCode.RepoCloning));

					if (repoManager.InUse)
						return Conflict(new ErrorMessageResponse(ErrorCode.RepoBusy));

					using var repo = await repoManager.LoadRepository(cancellationToken);

					// clone conflict
					if (repo != null)
						return Conflict(new ErrorMessageResponse(ErrorCode.RepoExists));

					var job = new Job
					{
						Description = String.Format(
							CultureInfo.InvariantCulture,
							"Clone{1} repository {0}",
							origin,
							cloneBranch != null
								? $"\"{cloneBranch}\" branch of"
								: String.Empty),
						StartedBy = AuthenticationContext.User,
						CancelRightsType = RightsType.Repository,
						CancelRight = (ulong)RepositoryRights.CancelClone,
						Instance = Instance,
					};
					var api = currentModel.ToApi();

					await DatabaseContext.Save(cancellationToken);
					await jobManager.RegisterOperation(
						job,
						async (core, databaseContextFactory, paramJob, progressReporter, ct) =>
						{
							var repoManager = core.RepositoryManager;
							using var repos = await repoManager.CloneRepository(
								origin,
								cloneBranch,
								currentModel.AccessUser,
								currentModel.AccessToken,
								progressReporter,
								currentModel.UpdateSubmodules.Value,
								ct)
							?? throw new JobException(ErrorCode.RepoExists);

							var instance = new Models.Instance
							{
								Id = Instance.Id,
							};
							await databaseContextFactory.UseContext(
								async databaseContext =>
								{
									databaseContext.Instances.Attach(instance);
									if (await PopulateApi(api, repos, databaseContext, instance, ct))
										await databaseContext.Save(ct);
								});
						},
						cancellationToken);

					api.Origin = model.Origin;
					api.Reference = model.Reference;
					api.ActiveJob = job.ToApi();

					return Created(api);
				});
		}

		/// <summary>
		/// Delete the repository.
		/// </summary>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the <see cref="IActionResult"/> of the operation.</returns>
		/// <response code="202">Job to delete the repository created successfully.</response>
		/// <response code="410">The database entity for the requested instance could not be retrieved. The instance was likely detached.</response>
		[HttpDelete]
		[TgsAuthorize(RepositoryRights.Delete)]
		[ProducesResponseType(typeof(RepositoryResponse), 202)]
		[ProducesResponseType(typeof(ErrorMessageResponse), 410)]
		public async Task<IActionResult> Delete(CancellationToken cancellationToken)
		{
			var currentModel = await DatabaseContext
				.RepositorySettings
				.AsQueryable()
				.Where(x => x.InstanceId == Instance.Id)
				.FirstOrDefaultAsync(cancellationToken);

			if (currentModel == default)
				return this.Gone();

			currentModel.AccessToken = null;
			currentModel.AccessUser = null;

			await DatabaseContext.Save(cancellationToken);

			Logger.LogInformation("Instance {instanceId} repository delete initiated by user {userId}", Instance.Id, AuthenticationContext.User.Id.Value);

			var job = new Job
			{
				Description = "Delete repository",
				StartedBy = AuthenticationContext.User,
				Instance = Instance,
			};
			var api = currentModel.ToApi();
			await jobManager.RegisterOperation(
				job,
				(core, databaseContextFactory, paramJob, progressReporter, ct) => core.RepositoryManager.DeleteRepository(ct),
				cancellationToken);
			api.ActiveJob = job.ToApi();
			return Accepted(api);
		}

		/// <summary>
		/// Get the repository's status.
		/// </summary>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the <see cref="IActionResult"/> of the operation.</returns>
		/// <response code="200">Retrieved the repository settings successfully.</response>
		/// <response code="201">Retrieved the repository settings successfully, though they did not previously exist.</response>
		/// <response code="410">The database entity for the requested instance could not be retrieved. The instance was likely detached.</response>
		[HttpGet]
		[TgsAuthorize(RepositoryRights.Read)]
		[ProducesResponseType(typeof(RepositoryResponse), 200)]
		[ProducesResponseType(typeof(RepositoryResponse), 201)]
		[ProducesResponseType(typeof(RepositoryResponse), 410)]
		public async Task<IActionResult> Read(CancellationToken cancellationToken)
		{
			var currentModel = await DatabaseContext
				.RepositorySettings
				.AsQueryable()
				.Where(x => x.InstanceId == Instance.Id)
				.FirstOrDefaultAsync(cancellationToken);

			if (currentModel == default)
				return this.Gone();

			var api = currentModel.ToApi();

			return await WithComponentInstance(
				async instance =>
				{
					var repoManager = instance.RepositoryManager;

					if (repoManager.CloneInProgress)
						return Conflict(new ErrorMessageResponse(ErrorCode.RepoCloning));

					if (repoManager.InUse)
						return Conflict(new ErrorMessageResponse(ErrorCode.RepoBusy));

					using var repo = await repoManager.LoadRepository(cancellationToken);
					if (repo != null && await PopulateApi(api, repo, DatabaseContext, Instance, cancellationToken))
					{
						// user may have fucked with the repo manually, do what we can
						await DatabaseContext.Save(cancellationToken);
						return Created(api);
					}

					return Json(api);
				});
		}

		/// <summary>
		/// Perform updates to the repository.
		/// </summary>
		/// <param name="model">The <see cref="RepositoryUpdateRequest"/>.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the <see cref="IActionResult"/> of the operation.</returns>
		/// <response code="200">Updated the repository settings successfully.</response>
		/// <response code="202">Updated the repository settings successfully and a <see cref="JobResponse"/> was created to make the requested git changes.</response>
		/// <response code="410">The database entity for the requested instance could not be retrieved. The instance was likely detached.</response>
		[HttpPost]
		[TgsAuthorize(
			RepositoryRights.ChangeAutoUpdateSettings
			| RepositoryRights.ChangeCommitter
			| RepositoryRights.ChangeCredentials
			| RepositoryRights.ChangeTestMergeCommits
			| RepositoryRights.MergePullRequest
			| RepositoryRights.SetReference
			| RepositoryRights.SetSha
			| RepositoryRights.UpdateBranch
			| RepositoryRights.ChangeSubmoduleUpdate)]
		[ProducesResponseType(typeof(RepositoryResponse), 200)]
		[ProducesResponseType(typeof(RepositoryResponse), 202)]
		[ProducesResponseType(typeof(ErrorMessageResponse), 410)]
#pragma warning disable CA1502 // TODO: Decomplexify
		public async Task<IActionResult> Update([FromBody] RepositoryUpdateRequest model, CancellationToken cancellationToken)
#pragma warning restore CA1502
		{
			if (model == null)
				throw new ArgumentNullException(nameof(model));

			if (model.AccessUser == null ^ model.AccessToken == null)
				return BadRequest(new ErrorMessageResponse(ErrorCode.RepoMismatchUserAndAccessToken));

			if (model.CheckoutSha != null && model.Reference != null)
				return BadRequest(new ErrorMessageResponse(ErrorCode.RepoMismatchShaAndReference));

			if (model.CheckoutSha != null && model.UpdateFromOrigin == true)
				return BadRequest(new ErrorMessageResponse(ErrorCode.RepoMismatchShaAndUpdate));

			if (model.NewTestMerges?.Any(x => model.NewTestMerges.Any(y => x != y && x.Number == y.Number)) == true)
				return BadRequest(new ErrorMessageResponse(ErrorCode.RepoDuplicateTestMerge));

			if (model.CommitterName?.Length == 0)
				return BadRequest(new ErrorMessageResponse(ErrorCode.RepoWhitespaceCommitterName));

			if (model.CommitterEmail?.Length == 0)
				return BadRequest(new ErrorMessageResponse(ErrorCode.RepoWhitespaceCommitterEmail));

			var newTestMerges = model.NewTestMerges != null && model.NewTestMerges.Count > 0;
			var userRights = (RepositoryRights)AuthenticationContext.GetRight(RightsType.Repository);
			if (newTestMerges && !userRights.HasFlag(RepositoryRights.MergePullRequest))
				return Forbid();

			var currentModel = await DatabaseContext
				.RepositorySettings
				.AsQueryable()
				.Where(x => x.InstanceId == Instance.Id)
				.FirstOrDefaultAsync(cancellationToken);

			if (currentModel == default)
				return this.Gone();

			bool CheckModified<T>(Expression<Func<Api.Models.Internal.RepositorySettings, T>> expression, RepositoryRights requiredRight)
			{
				var memberSelectorExpression = (MemberExpression)expression.Body;
				var property = (PropertyInfo)memberSelectorExpression.Member;

				var newVal = property.GetValue(model);
				if (newVal == null)
					return false;
				if (!userRights.HasFlag(requiredRight) && property.GetValue(currentModel) != newVal)
					return true;

				property.SetValue(currentModel, newVal);
				return false;
			}

			if (CheckModified(x => x.AccessToken, RepositoryRights.ChangeCredentials)
				|| CheckModified(x => x.AccessUser, RepositoryRights.ChangeCredentials)
				|| CheckModified(x => x.AutoUpdatesKeepTestMerges, RepositoryRights.ChangeAutoUpdateSettings)
				|| CheckModified(x => x.AutoUpdatesSynchronize, RepositoryRights.ChangeAutoUpdateSettings)
				|| CheckModified(x => x.CommitterEmail, RepositoryRights.ChangeCommitter)
				|| CheckModified(x => x.CommitterName, RepositoryRights.ChangeCommitter)
				|| CheckModified(x => x.PushTestMergeCommits, RepositoryRights.ChangeTestMergeCommits)
				|| CheckModified(x => x.CreateGitHubDeployments, RepositoryRights.ChangeTestMergeCommits)
				|| CheckModified(x => x.ShowTestMergeCommitters, RepositoryRights.ChangeTestMergeCommits)
				|| CheckModified(x => x.PostTestMergeComment, RepositoryRights.ChangeTestMergeCommits)
				|| CheckModified(x => x.UpdateSubmodules, RepositoryRights.ChangeSubmoduleUpdate)
				|| (model.UpdateFromOrigin == true && !userRights.HasFlag(RepositoryRights.UpdateBranch))
				|| (model.CheckoutSha != null && !userRights.HasFlag(RepositoryRights.SetSha))
				|| (model.Reference != null && model.UpdateFromOrigin != true && !userRights.HasFlag(RepositoryRights.SetReference))) // don't care if it's the same reference, we want to forbid them before starting the job
				return Forbid();

			if (model.AccessToken?.Length == 0 && model.AccessUser?.Length == 0)
			{
				// setting an empty string clears everything
				currentModel.AccessUser = null;
				currentModel.AccessToken = null;
			}

			var canRead = userRights.HasFlag(RepositoryRights.Read);

			var api = canRead ? currentModel.ToApi() : new RepositoryResponse();
			if (canRead)
			{
				var earlyOut = await WithComponentInstance(
				async instance =>
				{
					var repoManager = instance.RepositoryManager;
					if (repoManager.CloneInProgress)
						return Conflict(new ErrorMessageResponse(ErrorCode.RepoCloning));

					if (repoManager.InUse)
						return Conflict(new ErrorMessageResponse(ErrorCode.RepoBusy));

					using var repo = await repoManager.LoadRepository(cancellationToken);
					if (repo == null)
						return Conflict(new ErrorMessageResponse(ErrorCode.RepoMissing));
					await PopulateApi(api, repo, DatabaseContext, Instance, cancellationToken);

					return null;
				});

				if (earlyOut != null)
					return earlyOut;
			}

			// this is just db stuf so stow it away
			await DatabaseContext.Save(cancellationToken);

			// format the job description
			string description = null;
			if (model.UpdateFromOrigin == true)
				if (model.Reference != null)
					description = String.Format(CultureInfo.InvariantCulture, "Fetch and hard reset repository to origin/{0}", model.Reference);
				else if (model.CheckoutSha != null)
					description = String.Format(CultureInfo.InvariantCulture, "Fetch and checkout {0} in repository", model.CheckoutSha);
				else
					description = "Pull current repository reference";
			else if (model.Reference != null || model.CheckoutSha != null)
				description = String.Format(CultureInfo.InvariantCulture, "Checkout repository {0} {1}", model.Reference != null ? "reference" : "SHA", model.Reference ?? model.CheckoutSha);

			if (newTestMerges)
				description = String.Format(
					CultureInfo.InvariantCulture,
					"{0}est merge(s) {1}{2}",
					description != null
						? String.Format(CultureInfo.InvariantCulture, "{0} and t", description)
						: "T",
					String.Join(
						", ",
						model.NewTestMerges.Select(
							x => String.Format(
								CultureInfo.InvariantCulture,
								"#{0}{1}",
								x.Number,
								x.TargetCommitSha != null
									? String.Format(
										CultureInfo.InvariantCulture,
										" at {0}",
										x.TargetCommitSha[..7])
									: String.Empty))),
					description != null
						? String.Empty
						: " in repository");

			if (description == null)
				return Json(api); // no git changes

			var job = new Job
			{
				Description = description,
				StartedBy = AuthenticationContext.User,
				Instance = Instance,
				CancelRightsType = RightsType.Repository,
				CancelRight = (ulong)RepositoryRights.CancelPendingChanges,
			};

			var repositoryUpdater = new RepositoryUpdateService(
				model,
				currentModel,
				AuthenticationContext.User,
				loggerFactory.CreateLogger<RepositoryUpdateService>(),
				Instance.Id.Value);

			// Time to access git, do it in a job
			await jobManager.RegisterOperation(
				job,
				repositoryUpdater.RepositoryUpdateJob,
				cancellationToken);

			api.ActiveJob = job.ToApi();
			return Accepted(api);
		}

		/// <summary>
		/// Populate a given <paramref name="apiResponse"/> with the current state of a given <paramref name="repository"/>.
		/// </summary>
		/// <param name="apiResponse">The <see cref="RepositoryResponse"/> to populate.</param>
		/// <param name="repository">The <see cref="IRepository"/>.</param>
		/// <param name="databaseContext">The active <see cref="IDatabaseContext"/>.</param>
		/// <param name="instance">The active <see cref="Models.Instance"/>.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in <see langword="true"/> if the <paramref name="databaseContext"/> was modified in a way that requires saving, <see langword="false"/> otherwise.</returns>
		async Task<bool> PopulateApi(
			RepositoryResponse apiResponse,
			IRepository repository,
			IDatabaseContext databaseContext,
			Models.Instance instance,
			CancellationToken cancellationToken)
		{
			apiResponse.RemoteGitProvider = repository.RemoteGitProvider;
			apiResponse.RemoteRepositoryOwner = repository.RemoteRepositoryOwner;
			apiResponse.RemoteRepositoryName = repository.RemoteRepositoryName;

			apiResponse.Origin = repository.Origin;
			apiResponse.Reference = repository.Reference;

			// rev info stuff
			Models.RevisionInformation revisionInfo = null;
			var needsDbUpdate = await RepositoryUpdateService.LoadRevisionInformation(
				repository,
				databaseContext,
				Logger,
				instance,
				null,
				newRevInfo => revisionInfo = newRevInfo,
				cancellationToken);
			apiResponse.RevisionInformation = revisionInfo.ToApi();
			return needsDbUpdate;
		}
	}
}
