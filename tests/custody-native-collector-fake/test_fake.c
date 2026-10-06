/* Authored native controls; compile/run requires a separately selected window.
 * No real descriptor, process, clock or memory-file operation is used by tests.
 */
#define _GNU_SOURCE
#include <assert.h>
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
#include <sys/mman.h>
#include <sys/stat.h>
#include <time.h>
#include <limits.h>
#include <dirent.h>
#include <setjmp.h>
#include <stdarg.h>
#define DIE BPF_STMT(BPF_RET|BPF_K,SECCOMP_RET_KILL_PROCESS)
static jmp_buf refused;
static const char *reason;
static int fault,write_count,seal_count,inherit_count;
static unsigned char captured[4096];
static size_t captured_size;
static void reject(const char *value) { reason=value;longjmp(refused,1); }
static long injected_syscall(long number,...) {
 assert(number==__NR_memfd_create);
 va_list ap;va_start(ap,number);const char *name=va_arg(ap,const char*);unsigned int flags=va_arg(ap,unsigned int);va_end(ap);
 assert(!strcmp(name,"fsgg-native-collector-fake-filter"));assert(flags==(MFD_CLOEXEC|MFD_ALLOW_SEALING));
 return fault==1?-1:fault==2?4:3;
}
static ssize_t injected_write(int fd,const void *bytes,size_t size) {
 assert(fd==3);write_count++;
 if(fault==3)return 0;
 if(fault==10 && write_count==1){errno=EINTR;return -1;}
 size_t written=size>7?7:size;
 assert(captured_size+written<=sizeof(captured));memcpy(captured+captured_size,bytes,written);captured_size+=written;
 return (ssize_t)written;
}
static int injected_fcntl(int fd,int command,...) {
 assert(fd==3);va_list ap;va_start(ap,command);int result=0;
 if(command==F_ADD_SEALS){assert(va_arg(ap,int)==(F_SEAL_WRITE|F_SEAL_GROW|F_SEAL_SHRINK|F_SEAL_SEAL));seal_count++;result=fault==4?-1:0;}
 else if(command==F_GET_SEALS)result=fault==5?0:(F_SEAL_WRITE|F_SEAL_GROW|F_SEAL_SHRINK|F_SEAL_SEAL);
 else if(command==F_GETFD)result=fault==7?0:FD_CLOEXEC;
 else if(command==F_SETFD){assert(va_arg(ap,int)==0);inherit_count++;result=fault==8?-1:0;}
 else assert(0);
 va_end(ap);return result;
}
static off_t injected_lseek(int fd,off_t offset,int whence) { assert(fd==3&&offset==0&&whence==SEEK_SET);return fault==6?-1:0; }
static int injected_clock_gettime(clockid_t clock,struct timespec *now) {
 assert(clock==CLOCK_MONOTONIC);*now=(struct timespec){fault==9?2:0,0};return 0;
}
#define syscall injected_syscall
#define write injected_write
#define fcntl injected_fcntl
#define lseek injected_lseek
#define clock_gettime injected_clock_gettime
#include "../../src/FS.GG.Coordination.Orchestration.Execution/Custody/native-collector-fake.h"
#undef syscall
#undef write
#undef fcntl
#undef lseek
#undef clock_gettime

static void parser_and_recipe(void) {
 const char *op="0123456789abcdef0123456789abcdef";
 char *argv[]={"bootstrap",FAKE_MODE,(char*)op,"50000","/private/profile", "/private/0123456789abcdef0123456789abcdef","positive"};
 struct fake_request req;assert(fake_parse(7,argv,&req));
 assert(req.remaining_ms==50000 && !strcmp(req.operation,op));
 assert(!fake_parse(6,argv,&req));assert(!fake_parse(8,argv,&req));
 struct {int index;const char *bad;} cases[]={
  {1,"--arbitrary"},{2,"ABCDEF0123456789abcdef0123456789ab"},{2,"short"},
  {3,"0"},{3,"01"},{3,"+1"},{3,"60001"},{3,"1x"},{3,"999999999"},
  {4,"relative"},{4,"/profile/"},{4,"/profile/../other"},{4,"/profile//other"},{4,"/profile/\nother"},
  {4,"/private"},{5,"/private/wrong-id"},{6,"model"}};
 for(size_t i=0;i<sizeof(cases)/sizeof(cases[0]);i++) {
  char *saved=argv[cases[i].index];argv[cases[i].index]=(char*)cases[i].bad;
  assert(!fake_parse(7,argv,&req));argv[cases[i].index]=saved;
 }
 argv[4]="/private/literal $HOME ; profile";assert(fake_parse(7,argv,&req));
 char *args[FAKE_ARG_LIMIT];fake_recipe(args,&req,"/profile/fake.sh","/profile/packet.txt","/profile/schema.txt","/operation/scratch","/operation/private-canary.txt");
 size_t total=0;int filter=0,status=0,block=0;
 for(;args[total];total++) {
  assert(total<FAKE_ARG_LIMIT-1);
  assert(strcmp(args[total],"--disable-userns") && strcmp(args[total],"--share-net") && strcmp(args[total],"--proc") && strcmp(args[total],"--unshare-user-try"));
  if(!strcmp(args[total],"--seccomp")){assert(!strcmp(args[total+1],"3"));filter++;}
  if(!strcmp(args[total],"--json-status-fd")){assert(!strcmp(args[total+1],"2"));status++;}
  if(!strcmp(args[total],"--block-fd")){assert(!strcmp(args[total+1],"0"));block++;}
 }
 assert(filter==1&&status==1&&block==1&&total<FAKE_ARG_LIMIT);
 assert(!strcmp(args[0],"/usr/bin/bwrap")&&!strcmp(args[total-2],"positive"));
}
int main(void) {
 (void)fake_main;parser_and_recipe();
 for(fault=0;fault<=10;fault++) {
  reason=NULL;write_count=seal_count=inherit_count=0;captured_size=0;
  if(setjmp(refused)==0) {
   assert(fake_filter_fd(1000)==3);assert(fault==0||fault==10);
   assert(captured_size==sizeof(fake_rules)&&!memcmp(captured,fake_rules,sizeof(fake_rules)));
   assert(seal_count==1&&inherit_count==1);
  } else {assert(fault>=1&&fault<=9);assert(reason!=NULL);}
 }
 return 0;
}
