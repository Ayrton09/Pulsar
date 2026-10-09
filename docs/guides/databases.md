# Game databases

Many game plugins keep their data in MySQL or MariaDB (Oxide and Carbon plugins for Rust, CounterStrikeSharp plugins for
CS2, Minecraft plugins). Pulsar creates a database and a user for each server that needs one, on a database server you
run yourself; it does not install MySQL or MariaDB.

## Add a database host

1. Run MySQL 8 or MariaDB 10.6 or newer where both the panel and the game servers can reach it: on the panel's machine,
   on a node, or anywhere on your network.
2. Give Pulsar an account that may create databases and users and grant them, and nothing it does not need, for example:

   ```sql
   CREATE USER 'pulsar'@'<panel address>' IDENTIFIED BY '<a long random password>';
   GRANT ALL PRIVILEGES ON *.* TO 'pulsar'@'<panel address>' WITH GRANT OPTION;
   ```

3. Remove what a fresh MariaDB opens to every user: it grants the `test` database to the `PUBLIC` role, which every
   user has. Pulsar's users only get their own database, but whatever the server grants everyone, they get too:

   ```sql
   REVOKE ALL PRIVILEGES ON `test`.* FROM PUBLIC;
   REVOKE ALL PRIVILEGES ON `test\_%`.* FROM PUBLIC;
   DROP DATABASE IF EXISTS test;
   ```
4. In the panel, open **Database hosts** (it needs `settings.manage`) and add it. Pulsar connects before saving: a wrong
   address or password is refused with what the database server said.
   - **Address for game servers**: when game servers reach it at another address than the panel does. A database on a
     node is `127.0.0.1` for that node's games.
   - **Connections from**: where the databases' users may connect from. `%` is anywhere the database server can be
     reached; narrow it to your nodes' addresses (`10.0.0.%`) or keep the port closed to the internet in the firewall.
   - **Nodes**: tick some to offer the host to their servers only.

## Databases of a server

The server's **Databases** tab lists them. With `server.databases.manage` you create one (its name is
`s<short id>_<what you type>`, which is also its user), see its password, change it and delete it. Give the plugin the
address, name, user and password shown there. Changing the password stops the game from connecting until its
configuration has the new one.

A server may have two databases unless an administrator with `server.limits.edit` changes its limit (Settings, Resources).
Deleting a server drops its databases; if their host cannot be reached then, the audit log says which database was left
behind, to drop by hand.

## Backups

Pulsar's backups hold the server's files, not its databases. Back up your database server with its own tools
(`mariadb-dump`, `mysqldump`, or your hosting's snapshots).
