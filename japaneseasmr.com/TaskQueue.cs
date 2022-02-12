using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace japaneseasmr.com
{
    class TaskQueue<T>
    {
        public List<Task<T>> running_task_list = new List<Task<T>>();
        public List<Task<T>> done_task_list = new List<Task<T>>();
        private int queue_size = 0;//并发限制，即running_task_list大小限制
        public TaskQueue(int _size)
        {
            queue_size = _size;
        }
        public async Task Add(Task<T> t)
        {
            running_task_list.Add(t);
            await WaitUntil(queue_size);
        }
        public async Task Done()
        {
            await WaitUntil(0);
        }
        private async Task WaitUntil(int queue_target)//当队列大于target时清空
        {
            while(running_task_list.Count>queue_target)
            {
                var t=await Task.WhenAny(running_task_list);
                running_task_list.Remove(t);
                done_task_list.Add(t);
            }
        }
    }

}
