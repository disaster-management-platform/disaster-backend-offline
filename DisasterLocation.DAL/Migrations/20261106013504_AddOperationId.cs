using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DisasterLocation.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OperationId", table: "Desastres", type: "TEXT", nullable: true);
            migrationBuilder.CreateIndex(
                name: "IX_Desastres_OperationId", table: "Desastres",
                column: "OperationId", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Desastres_OperationId", table: "Desastres");
            migrationBuilder.DropColumn(name: "OperationId", table: "Desastres");
        }
    }
}
