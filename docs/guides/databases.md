# Game databases

Many game plugins keep their data in MySQL or MariaDB (Oxide and Carbon plugins for Rust, CounterStrikeSharp plugins for
CS2, Minecraft plugins), and some games in PostgreSQL. Pulsar creates a database and a user for each server that needs
one, on a database server you run yourself (or, for PostgreSQL, the one Pulsar's installer put on the panel's machine);
it does not install MySQL or MariaDB.

## Add a MySQL or MariaDB host

1. Run MySQL 8 or MariaDB 10.6 or newer where both the panel and the game servers can reach it: on the panel's machine,
   on a node, or anywhere on your network. **Use a database server for Pulsar's game databases only**, never one that
   holds other applications' data (a website's, a forum's) or accounts: the account Pulsar needs (step 2) can change,
   rename and drop the server's users (on MariaDB every one, `root` included: it could give `root@localhost` a new
   password; MySQL 8 keeps it from accounts with `SYSTEM_USER`, such as `root`, but not from the others), and the
   database names it may manage also match other applications' databases named like Pulsar's (a
   name that starts with `s` and holds an underscore, such as `shop_data`). So whoever takes over the panel, or reads
   that account's password from it, controls the whole database server; on a server of its own that is only the game
   servers' data.
2. Give Pulsar an account that may create databases and users and grant them, and nothing it does not need:

   ```sql
   CREATE USER 'pulsar'@'<panel address>' IDENTIFIED BY '<a long random password>';
   GRANT CREATE USER ON *.* TO 'pulsar'@'<panel address>';
   GRANT ALL PRIVILEGES ON `s%\_%`.* TO 'pulsar'@'<panel address>' WITH GRANT OPTION;
   ```

   Pulsar only creates and drops a database and a user for each server, changes those users' passwords and grants each
   user its own database. Every database (and user) it makes is named `s<server id>_<name>`: `s`, the server's short
   id, an underscore and the name. So the account needs `CREATE USER` on the server (to create, change and drop users)
   and every privilege, with the right to grant them, on databases named like that: `` `s%\_%` `` matches names that
   start with `s` and hold an underscore (`\_` is an underscore, not the one-character wildcard), which leaves out the
   server's own databases (`mysql`, `sys`, `information_schema`, `performance_schema`). Do not give it
   `ALL PRIVILEGES ON *.*`: that is the database server's root, which could read and change every other database and
   account on it. These grants are as narrow as MySQL and MariaDB allow, not a boundary: `CREATE USER` cannot be
   limited to some users, and `s%\_%` cannot tell Pulsar's databases from others named alike, hence a server of its own
   (step 1). This works on MySQL 8 and MariaDB 10.6 or newer, except on a MySQL with `partial_revokes` turned on,
   which takes wildcards in grants literally (it is off unless someone turned it on).

3. Remove what a fresh MariaDB opens to every user: it grants the `test` database to the `PUBLIC` role, which every
   user has. Pulsar's users only get their own database, but whatever the server grants everyone, they get too:

   ```sql
   REVOKE ALL PRIVILEGES ON `test`.* FROM PUBLIC;
   REVOKE ALL PRIVILEGES ON `test\_%`.* FROM PUBLIC;
   DROP DATABASE IF EXISTS test;
   ```
4. In the panel, open **Database hosts** (it needs `settings.manage`) and add it, as **MySQL or MariaDB**. Pulsar connects before saving: a wrong
   address or password is refused with what the database server said.
   - **Address for game servers**: when game servers reach it at another address than the panel does. A database on a
     node is `127.0.0.1` for that node's games.
   - **Connections from**: where the databases' users may connect from. `%` is anywhere the database server can be
     reached; narrow it to your nodes' addresses (`10.0.0.%`) or keep the port closed to the internet in the firewall.
   - **Require TLS**: the panel connects only with TLS. Without it, the panel uses TLS when the database server
     offers it and plain text otherwise, its account's password included. It is on by default for new hosts unless the
     address is this machine or your local network (`127.0.0.1`, `10.x`, `172.16.x`–`172.31.x`, `192.168.x`); hosts
     added before Pulsar had this setting keep it off until you turn it on. Pulsar does not check the server's
     certificate either way (home servers use self-signed ones), so a database server reached over the internet
     should also sit behind a VPN or tunnel. MySQL 8 and MariaDB 11.4 or newer offer TLS out of the box.
   - **Nodes**: tick some to offer the host to their servers only.

   Editing a host, an empty password keeps the saved one, as long as the address, port and user stay the same; change
   any of them and type the password again. What a host runs is chosen when it is added.

## Add a PostgreSQL host

PostgreSQL 16 or newer. The one Pulsar's installer puts on the panel's machine (18) does: game servers on that same
machine reach it at `127.0.0.1`.

1. As a superuser (`postgres`), give Pulsar a role that may create databases and roles, and nothing more:

   ```sql
   CREATE ROLE pulsar_games LOGIN CREATEDB CREATEROLE PASSWORD '<a long random password>';
   ```

   Not a superuser, and not the panel's own role (`pulsar`). From PostgreSQL 16, a role with `CREATEROLE` may change
   only the roles it created (older versions let it change every role but the superusers, the panel's included: Pulsar
   refuses them). Its password must be stored as SCRAM-SHA-256 (PostgreSQL's default since 14): Pulsar signs in with
   SCRAM only. Pulsar makes, for each server's database, a role of the same name that may sign in and nothing else
   (no superuser, no creating databases or roles) with at most 16 connections at once, the owner of a database made
   from `template0` that no other role may connect to; it sets the role's password as a SCRAM-SHA-256 verifier, never
   in clear. The panel's own database is closed to other roles too: Pulsar takes `PUBLIC`'s right to connect to it
   away when it updates, and its log warns at every start while it could not (an external database whose owner is not
   the panel's account: do it yourself, `REVOKE CONNECT, TEMPORARY ON DATABASE pulsar FROM PUBLIC;`). Every role can
   still see the names of the server's databases and roles (PostgreSQL shows them to all).

   A PostgreSQL upgraded from version 14 or older with `pg_upgrade` may still let every role create objects in the
   `public` schema of its `postgres` and `template1` databases; take that away there:
   `REVOKE CREATE ON SCHEMA public FROM PUBLIC;` (connected to each of them as a superuser).

   Each game server's role may hold 16 connections: keep the servers' total under the server's `max_connections`
   (100 unless changed), with room for the panel's own, or give the panel's role connections of its own
   (`reserved_connections` and `GRANT pg_use_reserved_connections TO pulsar;`).
2. Where game servers may connect from is PostgreSQL's own `pg_hba.conf`: the installer's PostgreSQL takes connections
   from the machine itself only, which suits games on the panel's machine. Game servers on other nodes need lines for
   their addresses there (and `listen_addresses`), with `scram-sha-256`, and the port open to those nodes alone.
3. In **Database hosts**, add it as **PostgreSQL** (port 5432) with that role and its password. **Require TLS** works as
   for MySQL, and then also binds the SCRAM sign-in to the TLS channel (`channel_binding=require`), so a man in the
   middle cannot relay it even though the certificate is not checked; the installer's PostgreSQL has no TLS, which is
   fine on `127.0.0.1`.

## Databases of a server

The server's **Databases** tab lists them. With `server.databases.manage` you create one (its name is
`s<short id>_<what you type>`, which is also its user), see its password, change it and delete it. Give the plugin the
address, name, user and password shown there. Changing the password stops the game from connecting until its
configuration has the new one. Showing a password is recorded in the audit log.

If the database server refuses or does not answer, the server's page only says so; what it said is in the panel's
log, and an administrator sees it when testing the host under **Database hosts**.

A server may have two databases unless an administrator with `server.limits.edit` changes its limit (Settings,
Resources; from 0 to 50, and an empty field keeps the current limit).
Deleting a server drops its databases; if their host cannot be reached then, the audit log says which database was left
behind, to drop by hand.

## Backups

Pulsar's backups hold the server's files, not its databases. Back up your database server with its own tools
(`mariadb-dump`, `mysqldump`, `pg_dump`, or your hosting's snapshots).
