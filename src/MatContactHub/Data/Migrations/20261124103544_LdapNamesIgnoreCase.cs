using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatContactHub.Data.Migrations
{
    /// <summary>
    /// LDAP compares ou and cn values without case: the LDAP name of an address book and the bind name of an LDAP account are kept in small letters, so their
    /// unique indexes are ones without case too (Kunden and kunden would be the same DN).
    /// </summary>
    public partial class LdapNamesIgnoreCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"AddressBook\" SET \"LdapName\" = lower(\"LdapName\") WHERE \"LdapName\" <> lower(\"LdapName\");");
            migrationBuilder.Sql("UPDATE \"LdapAccount\" SET \"Name\" = lower(\"Name\") WHERE \"Name\" <> lower(\"Name\");");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LdapAccount_Name_Lower",
                table: "LdapAccount",
                sql: "\"Name\" = lower(\"Name\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AddressBook_LdapName_Lower",
                table: "AddressBook",
                sql: "\"LdapName\" = lower(\"LdapName\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LdapAccount_Name_Lower",
                table: "LdapAccount");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AddressBook_LdapName_Lower",
                table: "AddressBook");
        }
    }
}
