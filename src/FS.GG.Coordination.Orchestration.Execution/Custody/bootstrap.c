#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <linux/audit.h>
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <poll.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <unistd.h>

/* No process descendants. Same-thread-group clones alone are supported. */
#define THREAD_REQUIRED (0x00000100U | 0x00000800U | 0x00010000U)
#define THREAD_ALLOWED (THREAD_REQUIRED | 0x00000200U | 0x00000400U | 0x00040000U | 0x00080000U | 0x00100000U | 0x00200000U | 0x01000000U)
#define DIE BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_KILL_PROCESS)
#define NR_UNAVAILABLE(n) BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,(n),0,1), BPF_STMT(BPF_RET|BPF_K,SECCOMP_RET_ERRNO | ENOSYS)
#define NR_DENY(n) BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,(n),0,1), DIE
static struct sock_filter rules[] = {
 BPF_STMT(BPF_LD|BPF_W|BPF_ABS, offsetof(struct seccomp_data,arch)),
 BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,AUDIT_ARCH_X86_64,1,0), DIE,
 BPF_STMT(BPF_LD|BPF_W|BPF_ABS, offsetof(struct seccomp_data,nr)),
 BPF_JUMP(BPF_JMP|BPF_JGE|BPF_K,0x40000000U,0,1), DIE,
 NR_DENY(__NR_fork), NR_DENY(__NR_vfork),
 NR_DENY(__NR_unshare), NR_DENY(__NR_setns),
 NR_UNAVAILABLE(__NR_io_uring_setup), NR_UNAVAILABLE(__NR_io_uring_enter), NR_UNAVAILABLE(__NR_io_uring_register),
 BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,__NR_clone3,0,1),
 BPF_STMT(BPF_RET|BPF_K,SECCOMP_RET_ERRNO | ENOSYS),
 /* Non-clone syscalls skip the flags validator and remain supported. */
 BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,__NR_clone,0,9),
 BPF_STMT(BPF_LD|BPF_W|BPF_ABS,offsetof(struct seccomp_data,args[0])+4),
 BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,0,1,0), DIE,
 BPF_STMT(BPF_LD|BPF_W|BPF_ABS,offsetof(struct seccomp_data,args[0])),
 BPF_JUMP(BPF_JMP|BPF_JSET|BPF_K,~THREAD_ALLOWED,0,1), DIE,
 BPF_STMT(BPF_ALU|BPF_AND|BPF_K,THREAD_REQUIRED),
 BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,THREAD_REQUIRED,1,0), DIE,
 BPF_STMT(BPF_RET|BPF_K,SECCOMP_RET_ALLOW)
};
static void reject(const char *reason) { dprintf(2,"custody-unavailable:%s\n",reason); _exit(125); }
static int number(const char *value, int maximum) {
 char *end = NULL; errno=0; long parsed=strtol(value,&end,10);
 if(errno || !value[0] || !end || *end || parsed<0 || parsed>maximum) reject("arguments");
 return (int)parsed;
}
#include "native-collector-fake.h"

int main(int argc, char **argv) {
 if(argc>1 && !strcmp(argv[1],FAKE_MODE)) fake_main(argc,argv);
 if(argc==2 && !strcmp(argv[1],"--export-native-collector-fake-filter")) {
  return write(1,fake_rules,sizeof(fake_rules))==(ssize_t)sizeof(fake_rules)?0:125;
 }
 if(argc==2 && !strcmp(argv[1],"--export-filter")) {
  if(write(1,rules,sizeof(rules))!=(ssize_t)sizeof(rules)) return 125;
  return 0;
 }
 if(argc<7 || strcmp(argv[1],"--bootstrap") || strlen(argv[2])!=32) reject("arguments");
 int milliseconds=number(argv[3],60000), environment_count=number(argv[4],128), argument_count=number(argv[5],128);
 if(milliseconds<1 || argument_count<1 || argc!=6+environment_count+argument_count) reject("arguments");
 size_t bytes=0;
 for(int i=6;i<argc;i++) { bytes+=strlen(argv[i])+1; if(bytes>65536) reject("argument-bytes"); }
 for(int i=0;i<environment_count;i++) if(!strchr(argv[6+i],'=') || argv[6+i][0]=='=') reject("environment");
 if(syscall(__NR_close_range,3U,~0U,0U)!=0) reject("closed-fds");
 /* Static executable, sanitized initial environment, no checker hooks before ACK. */
 if(dprintf(1,"FSGG-CUSTODY/1 %ld %s\n",(long)getpid(),argv[2])<0) reject("handshake-output");
 struct pollfd input={.fd=0,.events=POLLIN};
 int result;
 do { result=poll(&input,1,milliseconds); } while(result<0 && errno==EINTR);
 char ack=0;
 if(result!=1 || !(input.revents&POLLIN) || read(0,&ack,1)!=1 || ack!='A') reject("parent-ack");
 if(prctl(PR_SET_NO_NEW_PRIVS,1,0,0,0)!=0) reject("no-new-privs");
 struct sock_fprog program={.len=(unsigned short)(sizeof(rules)/sizeof(rules[0])),.filter=rules};
 if(prctl(PR_SET_SECCOMP,SECCOMP_MODE_FILTER,&program)!=0) reject("filter");
 if(dprintf(1,"FSGG-FILTERED/1 %s\n",argv[2])<0) reject("filter-output");
 /* The parent's acknowledgement channel is never exposed to checker code. */
 int null=open("/dev/null",O_RDONLY|O_CLOEXEC);
 if(null<0 || dup2(null,0)<0) reject("checker-input");
 if(null!=0) close(null);
 char *environment[129];
 for(int i=0;i<environment_count;i++) environment[i]=argv[6+i];
 environment[environment_count]=NULL;
 char *arguments[129];
 for(int i=0;i<argument_count;i++) arguments[i]=argv[6+environment_count+i];
 arguments[argument_count]=NULL;
 execve(arguments[0],arguments,environment);
 reject("checker-exec");
}
