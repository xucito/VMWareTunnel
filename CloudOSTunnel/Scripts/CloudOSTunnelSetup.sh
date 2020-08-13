#!/bin/bash

if [ $# -ne 2 ]
then
	echo "Please run this script with valid arguement such as username and hashed passwd"
	echo " ./tunnel_setup.sh tom T0M112e3d"
	exit 1
fi

user_name=$1
pass_wd=$2

grep "^#PubkeyAuthentication" /etc/ssh/sshd_config >/dev/null
if [ $? -eq 0 ]
then
	echo "Allowing Pubkey authentication and restarting sshd"
	sed -i 's/.*PubkeyAuthentication.*/PubkeyAuthentication yes/' /etc/ssh/sshd_config
	service sshd restart
else
	grep "PubkeyAuthentication yes" /etc/ssh/sshd_config >/dev/null
	if [ $? -ne 0 ]
	then
		echo "Allowing Pubkey authentication and restarting sshd"
		sed -i 's/.*PubkeyAuthentication.*/PubkeyAuthentication yes/' /etc/ssh/sshd_config
		service sshd restart
	fi
fi
# creating user
id $user_name > /dev/null 2>&1

if [ $? -ne 0 ]
then
	echo "Creating user"
	useradd -m $user_name -p "$pass_wd"
	if [ $? -eq 0 ]
	then
		echo "user created successfully and setting up passwd"
	fi
fi

if [ ! -d "/home/$user_name/.ssh" ]
then
 	echo "Creating .ssh directory"
	mkdir /home/${user_name}/.ssh && chmod 700 /home/${user_name}/.ssh && chown -R ${user_name} /home/${user_name}/.ssh
else
	ls -ld /home/${user_name}/.ssh/ | awk '{print $3 }' | grep "$user_name" > /dev/null 2>&1
	if [ $? -ne 0 ]
	then
		echo "Changing ownership of .ssh directory"
		chown -R $user_name /home/${user_name}/.ssh
	fi
fi

if [ ! -f "/home/$user_name/.ssh/authorized_keys" ]
then
	echo "Creating authorized_keys"
	touch /home/${user_name}/.ssh/authorized_keys && chown -R $user_name /home/${user_name}/.ssh/authorized_keys
fi

# setup sudo access
if [ -f /etc/SuSE-release ]
then
    grep "$user_name" /etc/sudoers > /dev/null
       if [ $? -ne 0 ]
    then
        echo "Setting up sudo access on Suse"
         echo "${user_name} ALL=(ALL) NOPASSWD: ALL" >> /etc/sudoers
    fi
else
  if [ ! -f /etc/sudoers.d/${user_name} ]
  then
    echo "Setting up sudo access"
    echo "${user_name}    ALL=(ALL)     NOPASSWD: ALL" > /etc/sudoers.d/$user_name
  fi
fi
