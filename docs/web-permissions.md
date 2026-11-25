# Managed website permissions

The installation root remains hardened. MCPanel does not grant Everyone, Users
or IIS write access to its executable or service binaries.

- The managed web root retains inheritable Modify permissions for IIS and SQL
  Server identities and the explicitly authorized panel operators (the Windows
  accounts used by ordinary shared and per-product Tomcat processes).
- The first interactive launch after an upgrade runs a one-time elevated
  repair of protected descendants and existing IIS application-pool identities.
  The completion marker is written only on success. Failures are recorded in
  StoreData\Work\web-permissions.log. Tray-only logon does not prompt for
  elevation; opening the panel triggers the repair.
- New installations, SVN updates and binding repairs check managed product
  ACLs. IIS also grants its actual pool identity Modify on its individual
  managed website, including protected descendants.
- Explicit repair skips reparse points and preserves inheritance settings and
  unrelated rules. Existing explicit deny entries are not overridden. Paths
  outside the managed web tree are not automatically granted access, including
  custom external products; configure those directories separately.

Validation on Windows: install a .NET product and save web.config from its
installation wizard; check create/delete of temporary files including on a
product whose ACL inheritance is disabled. Run a Java application under the
normal non-elevated panel operator and write to its application data directory.
Repeat after an online upgrade from 1.4.12 and after repairing bindings.
Check SQL Server can open a product database under web. Verify that IIS and
ordinary users cannot modify the MCPanel executable or service binaries.
Explicit denies, impersonation, read-only volumes/files, other security
software and externally configured service accounts may require separate
administrator configuration.
