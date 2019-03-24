# VMWareTunnel
Tunnel used for running scp and ssh commands through hypervisor.


# Pre-requisites

Uncomment in line /etc/ssh/sshd_config

`PubkeyAuthentication yes`

`AuthorizedKeysFile      .ssh/authorized_keys`
