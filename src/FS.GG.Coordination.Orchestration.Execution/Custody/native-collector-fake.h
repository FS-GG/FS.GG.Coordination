/* Closed, fake-only producer mode. No Host admission or generic launch API.
 * Included after bootstrap's existing reject/number helpers. Ordinary mode and
 * its rules remain unchanged. Generated native assets are separately admitted.
 */
#include <sys/mman.h>
#include <sys/stat.h>
#include <time.h>
#include <limits.h>
#include <dirent.h>

#define FAKE_MODE "--native-collector-fake-v1"
#define FAKE_PROFILE "untrusted-workload-userns-seccomp-v1"
#define FAKE_PATH_LIMIT 1024
#define FAKE_ARG_LIMIT 160

struct fake_request {
 const char *operation, *profile_root, *operation_root, *case_name;
 int remaining_ms;
};

static const char fake_fixture[] =
 "# Fixed qualification fixture only. No model, provider or authority credential.\n"
 "set -eu\n"
 "case \"$1\" in\n"
 " positive)\n"
 "  IFS= read -r packet < /input/packet.txt\n"
 "  [[ \"$packet\" == 'fixed synthetic packet' ]]\n"
 "  IFS= read -r schema < /input/schema.txt\n"
 "  [[ \"$schema\" == 'fixed synthetic schema' ]]\n"
 "  [[ ! -e /proc && ! -e /sys && ! -e /run && ! -e \"$2\" ]]\n"
 "  [[ \"$HOME\" == /scratch && \"$PWD\" == /scratch ]]\n"
 "  [[ -z \"${LD_PRELOAD+x}${LD_LIBRARY_PATH+x}${HTTPS_PROXY+x}${FSGG_TELEMETRY_TOKEN+x}\" ]]\n"
 "  for fd in {3..63}; do\n"
 "   if eval \": <&$fd\"; then printf 'unexpected inherited descriptor %s\\n' \"$fd\" >&2; exit 31; fi\n"
 "  done\n"
 "  if { printf 'mutation\\n' > /input/packet.txt; }; then exit 32; fi\n"
 "  if { printf 'mutation\\n' > /unbounded-root-write; }; then exit 34; fi\n"
 "  printf 'bounded scratch\\n' > /scratch/observed.txt\n"
 "  exec 2>&1\n"
 "  # Bash may abort its noninteractive shell on fork refusal. The EXIT trap is\n"
 "  # armed only after all earlier assertions. The outer decoder additionally\n"
 "  # requires the actual EPERM diagnostic; a generic failure is not accepted.\n"
 "  trap 'code=$?; trap - EXIT; if (( code == 0 )); then exit 33; fi; printf \"FIXED_POSITIVE_PRIVATE_FD_READONLY_FORK_REFUSAL\\n\"; exit 0' EXIT\n"
 "  ( : )\n"
 "  trap - EXIT\n"
 "  exit 33\n"
 "  ;;\n"
 " namespace)\n"
 "  exec /usr/bin/unshare --user /usr/bin/bash --noprofile --norc -c ':' 2>&1\n"
 "  ;;\n"
 " failure)\n"
 "  printf 'FIXED_INTENTIONAL_FAILURE\\n'; exit 17\n"
 "  ;;\n"
 " timeout)\n"
 "  printf 'FIXED_TIMEOUT_STARTED\\n'\n"
 "  while :; do :; done\n"
 "  ;;\n"
 " *) exit 99 ;;\n"
 "esac\n"
;
static const char fake_packet[] =
 "fixed synthetic packet\n"
;
static const char fake_schema[] =
 "fixed synthetic schema\n"
;
static const char fake_canary[] = "SYNTHETIC_PRIVATE_SENTINEL_ONLY\n";
/* Exact private single-thread filter; not the ordinary thread-capable rules. */
#define FAKE_ERRNO(n) BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,(n),0,1), BPF_STMT(BPF_RET|BPF_K,SECCOMP_RET_ERRNO | EPERM)
static struct sock_filter fake_rules[] = {
 BPF_STMT(BPF_LD|BPF_W|BPF_ABS,4),
 BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,AUDIT_ARCH_X86_64,1,0), DIE,
 BPF_STMT(BPF_LD|BPF_W|BPF_ABS,0),
 BPF_JUMP(BPF_JMP|BPF_JSET|BPF_K,0x40000000U,0,1), DIE,
 FAKE_ERRNO(56), FAKE_ERRNO(57), FAKE_ERRNO(58), FAKE_ERRNO(101),
 FAKE_ERRNO(165), FAKE_ERRNO(166), FAKE_ERRNO(246), FAKE_ERRNO(248),
 FAKE_ERRNO(249), FAKE_ERRNO(250), FAKE_ERRNO(272), FAKE_ERRNO(298),
 FAKE_ERRNO(304), FAKE_ERRNO(308), FAKE_ERRNO(310), FAKE_ERRNO(311), FAKE_ERRNO(321),
 BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K,435,0,1),
 BPF_STMT(BPF_RET|BPF_K,SECCOMP_RET_ERRNO | ENOSYS),
 BPF_STMT(BPF_RET|BPF_K,SECCOMP_RET_ALLOW)
};
#undef FAKE_ERRNO

static int fake_operation_valid(const char *value) {
 if(strnlen(value,33)!=32) return 0;
 for(size_t i=0;i<32;i++)
  if(!((value[i]>='0' && value[i]<='9') || (value[i]>='a' && value[i]<='f'))) return 0;
 return 1;
}
static int fake_path_valid(const char *path) {
 size_t n=strnlen(path,FAKE_PATH_LIMIT), begin=1;
 if(n<2 || n>=FAKE_PATH_LIMIT || path[0]!='/' || path[n-1]=='/') return 0;
 for(size_t i=1;i<=n;i++) {
  unsigned char c=(unsigned char)path[i];
  if(i<n && (c<32 || c==127)) return 0;
  if(c=='/' || c==0) {
   size_t width=i-begin;
   if(width==0 || (width==1 && path[begin]=='.') ||
      (width==2 && path[begin]=='.' && path[begin+1]=='.')) return 0;
   begin=i+1;
  }
 }
 return 1;
}
static int fake_below(const char *path,const char *parent) {
 size_t n=strlen(parent);
 return !strncmp(path,parent,n) && (path[n]=='/' || path[n]==0);
}
static int fake_parse(int argc,char **argv,struct fake_request *out) {
 if(argc!=7 || strcmp(argv[1],FAKE_MODE) || !fake_operation_valid(argv[2])) return 0;
 const char *limit=argv[3]; size_t n=strnlen(limit,6); int milliseconds=0;
 if(n<1 || n>5 || limit[0]=='0') return 0;
 for(size_t i=0;i<n;i++) {
  if(limit[i]<'0' || limit[i]>'9') return 0;
  milliseconds=milliseconds*10+(limit[i]-'0');
 }
 if(milliseconds<1 || milliseconds>60000 || !fake_path_valid(argv[4]) || !fake_path_valid(argv[5])) return 0;
 if(fake_below(argv[4],argv[5]) || fake_below(argv[5],argv[4])) return 0;
 if(strcmp(strrchr(argv[5],'/')+1,argv[2])) return 0;
 if(strcmp(argv[6],"positive") && strcmp(argv[6],"namespace") &&
    strcmp(argv[6],"failure") && strcmp(argv[6],"timeout")) return 0;
 *out=(struct fake_request){argv[2],argv[4],argv[5],argv[6],milliseconds};
 return 1;
}
static int64_t fake_now_ms(void) {
 struct timespec now;
 if(clock_gettime(CLOCK_MONOTONIC,&now)!=0) reject("fake-clock");
 return (int64_t)now.tv_sec*1000+now.tv_nsec/1000000;
}
static void fake_before(int64_t deadline) {
 if(fake_now_ms()>=deadline) reject("fake-original-preparation-deadline");
}
static void fake_join(char *output,size_t size,const char *root,const char *leaf) {
 int n=snprintf(output,size,"%s/%s",root,leaf);
 if(n<0 || (size_t)n>=size) reject("fake-role-path");
}
/* Traverse each component without following links. Fixed system roles require
 * root ownership; private roles admit only root/current user ancestors. This is
 * a trusted-controller boundary, not protection from another malicious same-UID
 * host process or privileged installation replacement. No model is on the host.
 */
static int fake_open(const char *path,int directory,int system_role,int64_t deadline) {
 char component[FAKE_PATH_LIMIT]; size_t n=strlen(path),begin=1;
 int parent=open("/",O_RDONLY|O_DIRECTORY|O_CLOEXEC|O_NOFOLLOW);
 if(parent<0) reject("fake-role-root");
 for(size_t i=1;i<=n;i++) if(path[i]=='/' || path[i]==0) {
  fake_before(deadline);
  size_t width=i-begin;
  if(width==0 || width>=sizeof(component)) reject("fake-role-component");
  memcpy(component,path+begin,width);component[width]=0;
  int last=(i==n);
  int fd=openat(parent,component,O_RDONLY|O_CLOEXEC|O_NOFOLLOW|O_NONBLOCK|
                ((!last || directory)?O_DIRECTORY:0));
  if(fd<0) reject("fake-role-open");
  struct stat st;
  if(fstat(fd,&st)!=0 || (st.st_mode&(S_IWGRP|S_IWOTH|S_ISUID|S_ISGID)) ||
     (st.st_uid!=0 && (system_role || st.st_uid!=getuid())) ||
     ((!last || directory)?!S_ISDIR(st.st_mode):!S_ISREG(st.st_mode))) reject("fake-role-identity");
  close(parent);parent=fd;begin=i+1;
 }
 return parent;
}
static void fake_private_directory(const char *path,int64_t deadline) {
 int fd=fake_open(path,1,0,deadline);struct stat st;
 if(fstat(fd,&st)!=0 || st.st_uid!=getuid() || (st.st_mode&0777)!=0700) reject("fake-private-directory");
 close(fd);
}
static void fake_empty_directory(const char *path,int64_t deadline) {
 int fd=fake_open(path,1,0,deadline);DIR *directory=fdopendir(fd);
 if(directory==NULL) reject("fake-scratch-directory");
 for(;;) {
  fake_before(deadline);errno=0;struct dirent *entry=readdir(directory);
  if(entry==NULL) { if(errno) reject("fake-scratch-read");break; }
  if(strcmp(entry->d_name,".") && strcmp(entry->d_name,"..")) reject("fake-scratch-not-empty");
 }
 if(closedir(directory)!=0) reject("fake-scratch-close");
}
static void fake_exact_file(const char *path,const char *expected,size_t size,int64_t deadline) {
 int fd=fake_open(path,0,0,deadline);struct stat st;char bytes[8192];
 if(size>sizeof(bytes) || fstat(fd,&st)!=0 || st.st_uid!=getuid() ||
    (st.st_mode&0777)!=0600 || st.st_nlink!=1 || st.st_size!=(off_t)size) reject("fake-input-shape");
 size_t used=0;
 while(used<size) {
  fake_before(deadline);ssize_t got=read(fd,bytes+used,size-used);
  if(got<0 && errno==EINTR) continue;
  if(got<=0) reject("fake-input-read");
  used+=(size_t)got;
 }
 if(memcmp(bytes,expected,size)) reject("fake-input-bytes");
 close(fd);
}
static void fake_system_file(const char *path,int executable,int64_t deadline) {
 int fd=fake_open(path,0,1,deadline);struct stat st;
 if(fstat(fd,&st)!=0 || st.st_size<=0 || (executable && !(st.st_mode&S_IXUSR))) reject("fake-system-input");
 close(fd);
}
static int fake_filter_fd(int64_t deadline) {
 int fd=(int)syscall(__NR_memfd_create,"fsgg-native-collector-fake-filter",MFD_CLOEXEC|MFD_ALLOW_SEALING);
 if(fd!=3) reject("fake-filter-fd");
 size_t used=0;
 while(used<sizeof(fake_rules)) {
  fake_before(deadline);
  ssize_t written=write(fd,((const char*)fake_rules)+used,sizeof(fake_rules)-used);
  if(written<0 && errno==EINTR) continue;
  if(written<=0) reject("fake-filter-write");
  used+=(size_t)written;
 }
 int seals=F_SEAL_WRITE|F_SEAL_GROW|F_SEAL_SHRINK|F_SEAL_SEAL;
 if(fcntl(fd,F_ADD_SEALS,seals)!=0 || fcntl(fd,F_GET_SEALS)!=seals ||
    lseek(fd,0,SEEK_SET)!=0) reject("fake-filter-seal");
 if(fcntl(fd,F_GETFD)!=FD_CLOEXEC || fcntl(fd,F_SETFD,0)!=0) reject("fake-filter-inheritance");
 return fd;
}
static void fake_push(char **args,size_t *count,const char *value) {
 if(*count>=FAKE_ARG_LIMIT-1) reject("fake-argv-bound");
 args[(*count)++]=(char*)value;args[*count]=NULL;
}
static void fake_recipe(char **args,const struct fake_request *request,
                        const char *fixture,const char *packet,const char *schema,
                        const char *scratch,const char *canary) {
 size_t count=0;
 #define PUSH(value) fake_push(args,&count,(value))
 PUSH("/usr/bin/bwrap");PUSH("--unshare-user");PUSH("--unshare-pid");PUSH("--unshare-ipc");
 PUSH("--unshare-uts");PUSH("--unshare-net");PUSH("--new-session");PUSH("--die-with-parent");
 PUSH("--cap-drop");PUSH("ALL");PUSH("--clearenv");
 PUSH("--dir");PUSH("/input");PUSH("--dir");PUSH("/lib64");PUSH("--dir");PUSH("/usr");
 PUSH("--dir");PUSH("/usr/bin");PUSH("--dir");PUSH("/usr/lib");
 #define READONLY(source,destination) PUSH("--ro-bind");PUSH(source);PUSH(destination)
 READONLY(fixture,"/fixture.sh");READONLY(packet,"/input/packet.txt");READONLY(schema,"/input/schema.txt");
 READONLY("/usr/lib/ld-linux-x86-64.so.2","/lib64/ld-linux-x86-64.so.2");
 READONLY("/usr/bin/bash","/usr/bin/bash");READONLY("/usr/bin/unshare","/usr/bin/unshare");
 READONLY("/usr/lib/ld-linux-x86-64.so.2","/usr/lib/ld-linux-x86-64.so.2");
 READONLY("/usr/lib/libc.so.6","/usr/lib/libc.so.6");
 READONLY("/usr/lib/libncursesw.so.6.6","/usr/lib/libncursesw.so.6");
 READONLY("/usr/lib/libreadline.so.8.3","/usr/lib/libreadline.so.8");
 PUSH("--bind");PUSH(scratch);PUSH("/scratch");PUSH("--remount-ro");PUSH("/");
 PUSH("--chdir");PUSH("/scratch");PUSH("--setenv");PUSH("HOME");PUSH("/scratch");
 PUSH("--setenv");PUSH("LANG");PUSH("C");PUSH("--setenv");PUSH("LC_ALL");PUSH("C");
 PUSH("--setenv");PUSH("PATH");PUSH("/usr/bin");
 PUSH("--json-status-fd");PUSH("2");PUSH("--block-fd");PUSH("0");PUSH("--seccomp");PUSH("3");
 PUSH("--");PUSH("/usr/bin/bash");PUSH("--noprofile");PUSH("--norc");PUSH("/fixture.sh");
 PUSH(request->case_name);PUSH(canary);
 #undef READONLY
 #undef PUSH
}
static void fake_main(int argc,char **argv) {
 struct fake_request request;
 if(!fake_parse(argc,argv,&request)) reject("fake-closed-request");
 if(getuid()==0 || getuid()!=geteuid() || getgid()!=getegid()) reject("fake-unprivileged-owner");
 int64_t deadline=fake_now_ms()+request.remaining_ms;
 if(syscall(__NR_close_range,3U,~0U,0U)!=0) reject("fake-closed-fds");
 fake_private_directory(request.profile_root,deadline);
 fake_private_directory(request.operation_root,deadline);
 char fixture[FAKE_PATH_LIMIT],packet[FAKE_PATH_LIMIT],schema[FAKE_PATH_LIMIT];
 char scratch[FAKE_PATH_LIMIT],canary[FAKE_PATH_LIMIT];
 fake_join(fixture,sizeof(fixture),request.profile_root,"fake.sh");
 fake_join(packet,sizeof(packet),request.profile_root,"packet.txt");
 fake_join(schema,sizeof(schema),request.profile_root,"schema.txt");
 fake_join(scratch,sizeof(scratch),request.operation_root,"scratch");
 fake_join(canary,sizeof(canary),request.operation_root,"private-canary.txt");
 fake_exact_file(fixture,fake_fixture,sizeof(fake_fixture)-1,deadline);
 fake_exact_file(packet,fake_packet,sizeof(fake_packet)-1,deadline);
 fake_exact_file(schema,fake_schema,sizeof(fake_schema)-1,deadline);
 fake_exact_file(canary,fake_canary,sizeof(fake_canary)-1,deadline);
 fake_private_directory(scratch,deadline);
 fake_empty_directory(scratch,deadline);
 static const char *system_inputs[]={"/usr/bin/bwrap","/usr/bin/bash","/usr/bin/unshare",
  "/usr/lib/ld-linux-x86-64.so.2","/usr/lib/libc.so.6","/usr/lib/libcap.so.2.78",
  "/usr/lib/libgcc_s.so.1","/usr/lib/libncursesw.so.6.6","/usr/lib/libreadline.so.8.3"};
 for(size_t i=0;i<sizeof(system_inputs)/sizeof(system_inputs[0]);i++) fake_system_file(system_inputs[i],i<3,deadline);
 if(prctl(PR_SET_NO_NEW_PRIVS,1,0,0,0)!=0) reject("fake-no-new-privs");
 (void)fake_filter_fd(deadline);
 char *args[FAKE_ARG_LIMIT];
 fake_recipe(args,&request,fixture,packet,schema,scratch,canary);
 char *environment[]={"PATH=/usr/bin","LANG=C","LC_ALL=C","HOME=/scratch",NULL};
 fake_before(deadline);
 execve("/usr/bin/bwrap",args,environment);
 reject("fake-bwrap-exec");
}
