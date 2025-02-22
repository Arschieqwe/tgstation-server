﻿using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tgstation.Server.Host.Database.Migrations
{
	/// <summary>
	/// Adds the InitialCompileJobId to the ReattachInformations table for MSSQL.
	/// </summary>
	public partial class MSAddReattachInfoInitialCompileJob : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			if (migrationBuilder == null)
				throw new ArgumentNullException(nameof(migrationBuilder));

			migrationBuilder.AddColumn<long>(
				name: "InitialCompileJobId",
				table: "ReattachInformations",
				type: "bigint",
				nullable: true);

			migrationBuilder.CreateIndex(
				name: "IX_ReattachInformations_InitialCompileJobId",
				table: "ReattachInformations",
				column: "InitialCompileJobId");

			migrationBuilder.AddForeignKey(
				name: "FK_ReattachInformations_CompileJobs_InitialCompileJobId",
				table: "ReattachInformations",
				column: "InitialCompileJobId",
				principalTable: "CompileJobs",
				principalColumn: "Id");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			if (migrationBuilder == null)
				throw new ArgumentNullException(nameof(migrationBuilder));

			migrationBuilder.DropForeignKey(
				name: "FK_ReattachInformations_CompileJobs_InitialCompileJobId",
				table: "ReattachInformations");

			migrationBuilder.DropIndex(
				name: "IX_ReattachInformations_InitialCompileJobId",
				table: "ReattachInformations");

			migrationBuilder.DropColumn(
				name: "InitialCompileJobId",
				table: "ReattachInformations");
		}
	}
}
