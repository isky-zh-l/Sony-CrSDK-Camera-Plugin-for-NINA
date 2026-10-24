#include "../src/download_events.h"
#include <thread>
#include <vector>
#include <set>
#include <string>
#include <cstdio>
int main(){
 sw_internal::Events queue;std::vector<std::thread> threads;
 for(int p=0;p<4;++p)threads.emplace_back([&,p]{for(int n=0;n<16;++n){auto path=L"C:/task-"+std::to_wstring(p)+L"-"+std::to_wstring(n)+L".ARW";queue.image(path.c_str());}});
 for(auto& t:threads)t.join();std::set<std::wstring> paths;
 queue.drain([&](const sw_internal::Event& e){paths.insert(e.path);});
 if(paths.size()!=64||queue.overflow.load())return 1;
 queue.image(L"C:/reuse.ARW");unsigned count=0;queue.drain([&](const sw_internal::Event&){++count;});if(count!=1)return 1;
 if(!sw_internal::fixed_iso(0x10000032)||sw_internal::fixed_iso(0x01000032)||sw_internal::fixed_iso(0xFFFFFF))return 1;
 std::puts("MAILBOX concurrent producers, reclamation, extended-fixed-ISO PASS");return 0;
}
