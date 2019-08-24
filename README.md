# VMWareTunnel
Tunnel used for running scp and ssh commands through hypervisor.


# Pre-requisites

Uncomment in line /etc/ssh/sshd_config

`PubkeyAuthentication yes`

`AuthorizedKeysFile      .ssh/authorized_keys`

# Setting up the ansible server
All settings can be set to default in the ansible.cfg except for the the settings explictly set below.

## Automatic Python Intepreter

In Ansible 2.8 an interactive python sessions is used to detect the version of python
https://docs.ansible.com/ansible/latest/reference_appendices/interpreter_discovery.html

As the tunnel has a limitation of single command execution, the intepreter should be set to the default in the ansible.cfg file.

`interpreter_python : /usr/bin/python`

## SCP Support

When connecting with the tunnel, the SCP protocol is support and not sftp or piped, therefore changing the setting in ansible.cfg to always use scp is required and will allow for faster execution

```
scp_if_ssh = True
transfer_method = scp
```

## Host Key Checking

Hosts and their associated keys cannot be consistently checked using the tunnel as IP addresses are used in key checks (via the tunnel, IPs are meaningless. Host Key Checking should be turned off in the ansible.cfg file.

`host_key_checking = False`

## TTY

Explicitly setting TTY to be used is required as pipelining should not be enabled.

`use_tty = True`
