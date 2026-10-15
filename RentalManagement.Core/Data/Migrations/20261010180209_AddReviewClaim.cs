using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalManagement.Core.Data.Migrations
{
    /// <summary>
    /// Bonus 2, review queue: adds the claim owner (FK to AspNetUsers, restricted) and claim time to RentalApplications,
    /// an index for the queue lookups, and a check constraint tying a claim to the Under Review status. Statuses are
    /// stored as strings, so the new <c>UnderReview</c> value needs no data change and existing rows (no claim, never
    /// Under Review) satisfy the constraint as they are.
    /// </summary>
    public partial class AddReviewClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "RentalApplications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByUserId",
                table: "RentalApplications",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_ClaimedByUserId",
                table: "RentalApplications",
                column: "ClaimedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalApplications_Status_ClaimedByUserId",
                table: "RentalApplications",
                columns: new[] { "Status", "ClaimedByUserId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RentalApplications_Claim",
                table: "RentalApplications",
                sql: "([Status] = 'UnderReview' AND [ClaimedByUserId] IS NOT NULL AND [ClaimedAt] IS NOT NULL) OR ([Status] <> 'UnderReview' AND [ClaimedByUserId] IS NULL AND [ClaimedAt] IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_RentalApplications_AspNetUsers_ClaimedByUserId",
                table: "RentalApplications",
                column: "ClaimedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RentalApplications_AspNetUsers_ClaimedByUserId",
                table: "RentalApplications");

            migrationBuilder.DropIndex(
                name: "IX_RentalApplications_ClaimedByUserId",
                table: "RentalApplications");

            migrationBuilder.DropIndex(
                name: "IX_RentalApplications_Status_ClaimedByUserId",
                table: "RentalApplications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RentalApplications_Claim",
                table: "RentalApplications");

            // The previous model has no Under Review status: put claimed applications back in the queue as Submitted
            // so rolling back never leaves a status the old code can't read.
            migrationBuilder.Sql("UPDATE [RentalApplications] SET [Status] = N'Submitted' WHERE [Status] = N'UnderReview';");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "ClaimedByUserId",
                table: "RentalApplications");
        }
    }
}
