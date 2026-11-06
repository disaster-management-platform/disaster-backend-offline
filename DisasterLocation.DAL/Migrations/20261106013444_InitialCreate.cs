using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DisasterLocation.DAL.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Desastres",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Ubicacion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Severidad = table.Column<int>(type: "INTEGER", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Latitud = table.Column<double>(type: "REAL", nullable: false),
                    Longitud = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Desastres", x => x.Id);
                    table.CheckConstraint("CK_Desastres_Latitud", "Latitud BETWEEN -90 AND 90");
                    table.CheckConstraint("CK_Desastres_Longitud", "Longitud BETWEEN -180 AND 180");
                    table.CheckConstraint("CK_Desastres_Severidad", "Severidad BETWEEN 1 AND 5");
                });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Desastres");
        }
    }
}
