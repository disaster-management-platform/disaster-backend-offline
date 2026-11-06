using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DisasterLocation.DAL.Migrations
{
    /// <inheritdoc />
    public partial class UseClientRequestId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "OperationId",
                table: "Desastres",
                newName: "ClientRequestId");

            migrationBuilder.DropIndex(name: "IX_Desastres_OperationId", table: "Desastres");
            migrationBuilder.CreateIndex(
                name: "IX_Desastres_ClientRequestId", table: "Desastres",
                column: "ClientRequestId", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ClientRequestId",
                table: "Desastres",
                newName: "OperationId");

            migrationBuilder.DropIndex(name: "IX_Desastres_ClientRequestId", table: "Desastres");
            migrationBuilder.CreateIndex(
                name: "IX_Desastres_OperationId", table: "Desastres",
                column: "OperationId", unique: true);
        }
    }
}
